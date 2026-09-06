//! Private lifecycle control RPCs.
//!
//! The service is wired into the real TBEP listener so callers cannot stop at
//! an unused adapter. Native lifecycle composition is explicit and invoked by
//! the production listener when supplied.

use tonic::{Code, Request, Response, Status};

use super::{TbepGrpcServer, authenticate_gateway_certificate, metadata_value};
use crate::lifecycle;
use crate::proto::terrabase::v1 as pb;
use pb::lifecycle_control_service_server::{
    LifecycleControlService, LifecycleControlServiceServer,
};

impl TbepGrpcServer {
    fn authenticate_lifecycle<T>(&self, request: &Request<T>) -> Result<(), Status> {
        let principal = metadata_value(
            request.metadata(),
            super::PRINCIPAL_METADATA,
            super::MAX_PRINCIPAL_BYTES,
        )?;
        if !self.config.gateway_principals.contains(&principal) {
            return Err(Status::new(
                Code::PermissionDenied,
                "principal is not an admitted orchestrator",
            ));
        }
        let certificates = request
            .peer_certs()
            .ok_or_else(|| Status::new(Code::Unauthenticated, "lifecycle control requires mTLS"))?;
        let leaf = certificates
            .first()
            .ok_or_else(|| Status::new(Code::Unauthenticated, "lifecycle control requires mTLS"))?;
        authenticate_gateway_certificate(&principal, leaf)
    }
}

#[tonic::async_trait]
impl LifecycleControlService for std::sync::Arc<TbepGrpcServer> {
    async fn restore_to_target(
        &self,
        request: Request<pb::RestoreToTargetRequest>,
    ) -> Result<Response<pb::LifecycleControlResponse>, Status> {
        self.authenticate_lifecycle(&request)?;
        let request = request.into_inner();
        if request.operation_id.is_empty()
            || request.restore_epoch == 0
            || request.source_owner.is_empty()
            || request.source_database.is_empty()
            || request.recovery_point_id.is_empty()
            || request.recovery_point_digest.len() != 64
            || request.target_owner.is_empty()
            || request.target_database.is_empty()
            || request.journal_cut.is_empty()
        {
            return Err(Status::invalid_argument("restore request is incomplete"));
        }
        let Some(owner) = self.lifecycle.as_ref() else {
            return Ok(Response::new(lifecycle::unavailable(&request.operation_id)));
        };
        let response = owner
            .restore_to_target(&request)
            .await
            .map_err(|error| Status::unavailable(error))?;
        Ok(Response::new(response))
    }

    async fn ensure_snapshot(
        &self,
        request: Request<pb::LifecycleControlRequest>,
    ) -> Result<Response<pb::LifecycleControlResponse>, Status> {
        self.dispatch(request, |owner, context, operation| {
            owner.ensure_snapshot(context, operation)
        })
        .await
    }

    async fn verify_snapshot_placement(
        &self,
        request: Request<pb::LifecycleControlRequest>,
    ) -> Result<Response<pb::LifecycleControlResponse>, Status> {
        self.dispatch(request, |owner, context, operation| {
            owner.verify_snapshot_placement(context, operation)
        })
        .await
    }

    async fn close_database(
        &self,
        request: Request<pb::LifecycleControlRequest>,
    ) -> Result<Response<pb::LifecycleControlResponse>, Status> {
        self.dispatch(request, |owner, context, operation| owner.close_database(context, operation))
            .await
    }

    async fn stop_native_backup(
        &self,
        request: Request<pb::LifecycleControlRequest>,
    ) -> Result<Response<pb::LifecycleControlResponse>, Status> {
        self.dispatch(request, |owner, context, operation| {
            owner.stop_native_backup(context, operation)
        })
        .await
    }

    async fn reactivate(
        &self,
        request: Request<pb::LifecycleReactivateRequest>,
    ) -> Result<Response<pb::LifecycleControlResponse>, Status> {
        self.authenticate_lifecycle(&request)?;
        let request = request.into_inner();
        let operation = lifecycle::validate_reactivate(&request).map_err(|_| {
            Status::new(Code::InvalidArgument, "lifecycle restore target is invalid")
        })?;
        let inner = request.request.as_ref().expect("validated reactivation request");
        let Some(owner) = self.lifecycle.as_ref() else {
            return Ok(Response::new(lifecycle::unavailable(operation)));
        };
        let response = owner
            .reactivate(inner.context.as_ref().expect("validated lifecycle context"), operation)
            .await
            .map_err(|error| Status::new(Code::Unavailable, error))?;
        Ok(Response::new(response))
    }

    async fn delete_owned_data(
        &self,
        request: Request<pb::LifecycleControlRequest>,
    ) -> Result<Response<pb::LifecycleControlResponse>, Status> {
        self.dispatch(request, |owner, context, operation| {
            owner.delete_owned_data(context, operation)
        })
        .await
    }
}

impl TbepGrpcServer {
    async fn dispatch(
        &self,
        request: Request<pb::LifecycleControlRequest>,
        invoke: impl for<'a> FnOnce(
            &'a dyn lifecycle::LifecycleControl,
            &'a pb::LifecycleControlContext,
            &'a str,
        ) -> lifecycle::LifecycleFuture<'a>,
    ) -> Result<Response<pb::LifecycleControlResponse>, Status> {
        self.authenticate_lifecycle(&request)?;
        let request = request.into_inner();
        let operation_id = lifecycle::validate_request(&request)
            .map_err(|_| Status::new(Code::InvalidArgument, "lifecycle identity is invalid"))?;
        let Some(owner) = self.lifecycle.as_ref() else {
            return Ok(Response::new(lifecycle::unavailable(operation_id)));
        };
        let context = request.context.as_ref().expect("validated lifecycle context");
        let response = invoke(owner.as_ref(), context, operation_id)
            .await
            .map_err(|error| Status::new(Code::Unavailable, error))?;
        Ok(Response::new(response))
    }
}

/// Registers the lifecycle service on a TBEP router.
pub fn server(
    server: std::sync::Arc<TbepGrpcServer>,
) -> LifecycleControlServiceServer<std::sync::Arc<TbepGrpcServer>> {
    LifecycleControlServiceServer::new(server)
}
