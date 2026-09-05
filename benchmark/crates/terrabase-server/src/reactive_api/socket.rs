//! Optional WebSocket delivery over the same immutable view log as HTTP.
use super::{
    ApiError, ApiState, AttachRequest, ReadResult, WsPermit, checkpoint_bytes,
    checkpoint_bytes_with_identity, constant_time_equal, encode_base64, error_bytes, prepare_view,
    store,
};
use axum::extract::ws::{Message as SocketMessage, WebSocket};
use std::{collections::BTreeMap, sync::Arc, time::Duration};
use terrabase_sync_core::wire::{ErrorCode, Message, decode_envelope};
use tokio::task::{AbortHandle, JoinSet};

const MAX_SUBSCRIPTIONS: usize = 64;
const SEND_TIMEOUT: Duration = Duration::from_secs(5);

type Delivery = (String, u64, Result<ReadResult, ApiError>);
struct Subscription {
    view: String,
    generation: u64,
    offset: u64,
    ticket: u64,
    pending: AbortHandle,
}

struct Session {
    state: Arc<ApiState>,
    database: String,
    principal: crate::api_keys::Principal,
    credential: String,
    subscriptions: BTreeMap<String, Subscription>,
    aliases: BTreeMap<String, String>,
    deliveries: JoinSet<Delivery>,
    next_ticket: u64,
}

pub(super) async fn serve(
    mut socket: WebSocket,
    state: Arc<ApiState>,
    database: String,
    principal: crate::api_keys::Principal,
    credential: String,
    _permit: WsPermit,
) {
    let mut session = Session {
        state,
        database,
        principal,
        credential,
        subscriptions: BTreeMap::new(),
        aliases: BTreeMap::new(),
        deliveries: JoinSet::new(),
        next_ticket: 0,
    };
    let mut authorization = tokio::time::interval(Duration::from_secs(1));
    let result = loop {
        let step = tokio::select! {
            incoming = socket.recv() => {
                match incoming {
                    Some(Ok(SocketMessage::Binary(bytes))) => session.receive(&mut socket, &bytes).await,
                    Some(Ok(SocketMessage::Ping(bytes))) => send(&mut socket, SocketMessage::Pong(bytes)).await,
                    Some(Ok(SocketMessage::Pong(_))) => Ok(()),
                    Some(Ok(SocketMessage::Text(_))) => Err(ErrorCode::MalformedFrame),
                    _ => break Ok(()),
                }
            }
            ready = session.deliveries.join_next(), if !session.deliveries.is_empty() => {
                match ready {
                    Some(Ok(delivery)) => session.deliver(&mut socket, delivery).await,
                    Some(Err(error)) if !error.is_cancelled() => Err(ErrorCode::Unavailable),
                    _ => Ok(()),
                }
            }
            _ = authorization.tick() => session.authorized(),
        };
        if let Err(error) = step {
            break Err(error);
        }
    };
    // Dropping the JoinSet aborts every outstanding shared-poll follower.
    drop(session);
    if matches!(result, Err(ErrorCode::SlowConsumer)) {
        return;
    }
    if let Err(error) = result {
        let _ = send(&mut socket, SocketMessage::Binary(error_bytes(error).into())).await;
    }
    let _ = send(&mut socket, SocketMessage::Close(None)).await;
}

async fn send(socket: &mut WebSocket, message: SocketMessage) -> Result<(), ErrorCode> {
    tokio::time::timeout(SEND_TIMEOUT, socket.send(message))
        .await
        .map_err(|_| ErrorCode::SlowConsumer)?
        .map_err(|_| ErrorCode::Unavailable)
}

impl Session {
    fn authorized(&self) -> Result<(), ErrorCode> {
        let admitted = self
            .state
            .keys
            .as_ref()
            .ok_or(ErrorCode::AuthenticationExpired)?
            .admit(&self.credential)
            .map_err(|_| ErrorCode::AuthenticationExpired)?;
        if admitted != self.principal
            || !admitted.scope().admits(crate::api_keys::Verbs::READ, &self.database)
        {
            return Err(ErrorCode::PermissionDenied);
        }
        Ok(())
    }

    fn unsubscribe(&mut self, subscription: &str) {
        let view = if let Some(view) = self.aliases.remove(subscription) {
            if self.aliases.values().any(|other| other == &view) {
                return;
            }
            view
        } else {
            self.aliases.retain(|_, view| view != subscription);
            subscription.to_owned()
        };
        if let Some(active) = self.subscriptions.remove(&view) {
            active.pending.abort();
        }
    }

    async fn receive(&mut self, socket: &mut WebSocket, bytes: &[u8]) -> Result<(), ErrorCode> {
        self.authorized()?;
        match decode_envelope(bytes).map_err(|_| ErrorCode::MalformedFrame)?.message {
            Message::Subscribe { subscription, query, parameters, checkpoint } => {
                self.subscribe(socket, subscription, query, parameters, checkpoint).await
            }
            Message::Unsubscribe { subscription } => {
                self.unsubscribe(&subscription);
                Ok(())
            }
            Message::CheckpointAck { subscription, checkpoint } => {
                let view = self.aliases.get(&subscription).unwrap_or(&subscription);
                let active = self.subscriptions.get(view).ok_or(ErrorCode::CheckpointRefused)?;
                if checkpoint_offset(&active.view, active.generation, &checkpoint)? > active.offset
                {
                    return Err(ErrorCode::CheckpointRefused);
                }
                Ok(())
            }
            Message::Heartbeat => Ok(()),
            _ => Err(ErrorCode::MalformedFrame),
        }
    }

    async fn subscribe(
        &mut self,
        socket: &mut WebSocket,
        subscription: String,
        query: String,
        parameters: Vec<u8>,
        checkpoint: Option<Vec<u8>>,
    ) -> Result<(), ErrorCode> {
        if self.deliveries.len() >= MAX_SUBSCRIPTIONS * 2
            || (!self.aliases.contains_key(&subscription)
                && self.aliases.len() >= MAX_SUBSCRIPTIONS)
        {
            return Err(ErrorCode::Overloaded);
        }
        let request = AttachRequest {
            database_id: self.database.clone(),
            statement: query,
            parameters: encode_base64(&parameters),
            checkpoint: checkpoint.as_ref().map(|value| encode_base64(value)),
            subscription: Some(subscription.clone()),
            entitlement_epoch: 0,
        };
        let scope = format!("{}:{}:{}", self.state.scope.0, self.state.scope.1, self.database);
        let host = crate::api::document_host(&self.state, &self.database)
            .map_err(|_| ErrorCode::PermissionDenied)?;
        crate::api::check_read_guard(
            &self.state,
            &self.database,
            terrabase::observability::Operation::Subscribe,
        )
        .map_err(|_| ErrorCode::Overloaded)?;
        let prepared = prepare_view(&self.state, &host, &request, &scope, Some(&self.principal))
            .map_err(|_| ErrorCode::PermissionDenied)?;
        let (attached, view) = store()
            .attach_materialized(
                &request,
                &scope,
                self.state.region,
                Some(&prepared),
                &self.state.capability_keys,
                Some(&self.state.guards),
            )
            .map_err(|_| ErrorCode::Unavailable)?;
        let resume = checkpoint
            .as_ref()
            .map(|checkpoint| checkpoint_offset(&view, attached.generation, checkpoint))
            .transpose()?;
        let offset =
            if let Some(offset) = resume.filter(|_| !self.subscriptions.contains_key(&view)) {
                offset
            } else {
                let snapshot = store()
                    .read_snapshot(&view, attached.generation, attached.offset)
                    .map_err(|_| ErrorCode::Unavailable)?;
                self.authorized()?;
                send(socket, SocketMessage::Binary(snapshot.bytes.to_vec().into())).await?;
                attached.offset
            };
        if self.aliases.contains_key(&subscription) {
            self.unsubscribe(&subscription);
        }
        if let Some(previous) = self.subscriptions.remove(&view) {
            previous.pending.abort();
        }
        self.aliases.insert(subscription, view.clone());
        let ticket = self.next_ticket;
        self.next_ticket = ticket.checked_add(1).ok_or(ErrorCode::Unavailable)?;
        let pending =
            schedule(&mut self.deliveries, &view, ticket, &view, attached.generation, offset);
        self.subscriptions.insert(
            view.clone(),
            Subscription { view, generation: attached.generation, offset, ticket, pending },
        );
        Ok(())
    }

    async fn deliver(
        &mut self,
        socket: &mut WebSocket,
        (subscription, ticket, result): Delivery,
    ) -> Result<(), ErrorCode> {
        self.authorized()?;
        let Some(active) =
            self.subscriptions.get_mut(&subscription).filter(|active| active.ticket == ticket)
        else {
            return Ok(());
        };
        let bytes = match result.map_err(|_| ErrorCode::Unavailable)? {
            ReadResult::Data { revision, frozen } => {
                active.offset = revision;
                frozen.bytes.to_vec()
            }
            ReadResult::Reset(bytes) => {
                let Message::Reset { snapshot, .. } =
                    decode_envelope(&bytes).map_err(|_| ErrorCode::Unavailable)?.message
                else {
                    return Err(ErrorCode::Unavailable);
                };
                active.offset = snapshot.revision;
                bytes
            }
            ReadResult::Empty | ReadResult::Deferred(_) => {
                terrabase_sync_core::wire::encode_envelope(&terrabase_sync_core::wire::Envelope {
                    features: 0,
                    correlation: 0,
                    causation: 0,
                    message: Message::Heartbeat,
                })
                .map_err(|_| ErrorCode::Unavailable)?
            }
        };
        send(socket, SocketMessage::Binary(bytes.into())).await?;
        active.pending = schedule(
            &mut self.deliveries,
            &subscription,
            ticket,
            &active.view,
            active.generation,
            active.offset,
        );
        Ok(())
    }
}

fn schedule(
    deliveries: &mut JoinSet<Delivery>,
    subscription: &str,
    ticket: u64,
    view: &str,
    generation: u64,
    offset: u64,
) -> AbortHandle {
    let subscription = subscription.to_owned();
    let view = view.to_owned();
    deliveries.spawn(async move {
        (subscription, ticket, store().poll_log(&view, generation, offset).await)
    })
}

fn checkpoint_offset(view: &str, generation: u64, checkpoint: &[u8]) -> Result<u64, ErrorCode> {
    let views = store().views.lock().map_err(|_| ErrorCode::Unavailable)?;
    let active = views
        .get(view)
        .filter(|active| active.generation == generation)
        .ok_or(ErrorCode::CheckpointRefused)?;
    let prefix = checkpoint_bytes(view, generation, 0).len();
    if checkpoint.len() != prefix + 16 {
        return Err(ErrorCode::CheckpointRefused);
    }
    let offset = u64::from_be_bytes(
        checkpoint[prefix - 8..prefix].try_into().map_err(|_| ErrorCode::CheckpointRefused)?,
    );
    if offset > active.head
        || !constant_time_equal(
            checkpoint,
            &checkpoint_bytes_with_identity(view, generation, offset, &active.identity_digest),
        )
    {
        return Err(ErrorCode::CheckpointRefused);
    }
    Ok(offset)
}
