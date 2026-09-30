use serde_json::json;
use std::{
    env,
    process::ExitCode,
    sync::Arc,
    time::{SystemTime, UNIX_EPOCH},
};
use terrascale::*;

struct Fixture {
    origin: String,
    key: String,
    deny_key: Option<String>,
    ca_pem: Option<Vec<u8>>,
    scope: DatabaseScope,
    collection: CollectionId,
    document: DocumentId,
    schema_generation: DecimalU64,
    schema_digest: Digest,
    field: u32,
    kind: String,
}

fn required(name: &str) -> Result<String> {
    env::var(name)
        .ok()
        .filter(|v| !v.trim().is_empty())
        .ok_or_else(|| {
            Error::new(
                ErrorKind::Validation,
                "missing_fixture",
                format!("Missing {name}."),
            )
        })
}
fn parse<T: std::str::FromStr<Err = Error>>(name: &str) -> Result<T> {
    required(name)?.parse()
}
impl Fixture {
    fn load() -> Result<Self> {
        let field = required("TS_NATIVE_FIELD_ID")?
            .parse::<u32>()
            .map_err(|_| {
                Error::new(
                    ErrorKind::Validation,
                    "invalid_fixture",
                    "Invalid field ID.",
                )
            })?;
        let kind = required("TS_NATIVE_FIELD_TYPE")?;
        if field == 0 || !matches!(kind.as_str(), "i64" | "utf8") {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_fixture",
                "Fixture field requires positive ID and i64 or utf8 type.",
            ));
        }
        Ok(Self {
            origin: required("TS_NATIVE_ORIGIN")?,
            key: required("TS_NATIVE_API_KEY")?,
            deny_key: env::var("TS_NATIVE_DENY_API_KEY").ok(),
            ca_pem: env::var_os("TS_NATIVE_CA_PEM")
                .map(|path| {
                    std::fs::read(path).map_err(|_| {
                        Error::new(
                            ErrorKind::Validation,
                            "invalid_fixture_ca",
                            "Cannot read fixture CA PEM.",
                        )
                    })
                })
                .transpose()?,
            scope: DatabaseScope {
                tenant: parse("TS_NATIVE_TENANT")?,
                database: parse("TS_NATIVE_DATABASE")?,
                shard: parse("TS_NATIVE_SHARD")?,
            },
            collection: parse("TS_NATIVE_COLLECTION")?,
            document: parse("TS_NATIVE_DOCUMENT")?,
            schema_generation: parse("TS_NATIVE_SCHEMA_GENERATION")?,
            schema_digest: parse("TS_NATIVE_SCHEMA_DIGEST")?,
            field,
            kind,
        })
    }
    fn client(&self, key: &str) -> Result<TerraScaleClient> {
        let config = ClientConfig::builder()
            .api_base_url(&self.origin)
            .api_key(key)
            .max_retry_attempts(0)
            .build()?;
        match &self.ca_pem {
            Some(pem) => Ok(TerraScaleClient::with_transport(
                config,
                Arc::new(ReqwestTransport::with_root_certificates(pem)?),
            )),
            None => Ok(TerraScaleClient::new(config)),
        }
    }
    fn atlas_client(&self) -> Result<TerraScaleClient> {
        let config = ClientConfig::builder()
            .api_key(&self.key)
            .max_retry_attempts(0)
            .build()?;
        match &self.ca_pem {
            Some(pem) => Ok(TerraScaleClient::with_transport(
                config,
                Arc::new(ReqwestTransport::with_root_certificates(pem)?),
            )),
            None => Ok(TerraScaleClient::new(config)),
        }
    }
    fn value(&self, number: i64) -> Value {
        if self.kind == "i64" {
            Value::I64(number)
        } else {
            Value::Utf8(format!("rust-native-{number}"))
        }
    }
    fn fields(&self, number: i64) -> Result<Fields> {
        Fields::new(vec![Field {
            id: self.field,
            value: self.value(number),
        }])
    }
    fn write(
        &self,
        document: DocumentId,
        condition: Condition,
        observed: Vec<Observed>,
        value: WriteValue,
    ) -> Write {
        Write {
            collection: self.collection,
            document,
            schema_generation: self.schema_generation,
            schema_digest: self.schema_digest,
            observed,
            condition,
            value,
        }
    }
    fn prepared(&self, writes: Vec<Write>) -> Result<PreparedTransaction> {
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(|_| {
                Error::new(
                    ErrorKind::Validation,
                    "invalid_clock",
                    "System clock precedes Unix epoch.",
                )
            })?
            .as_millis();
        let expiry = u64::try_from(now)
            .map_err(|_| {
                Error::new(
                    ErrorKind::Validation,
                    "invalid_clock",
                    "System clock overflow.",
                )
            })?
            .checked_add(60_000)
            .ok_or_else(|| {
                Error::new(
                    ErrorKind::Validation,
                    "invalid_clock",
                    "System clock overflow.",
                )
            })?;
        PreparedTransaction::new(
            self.scope,
            TransactionId::new(expiry),
            Authority::Leaderless {},
            writes,
        )
    }
    fn predicate(&self, number: i64) -> Predicate {
        Predicate::Equal {
            path: vec![self.field],
            value: self.value(number),
        }
    }
}

fn check(condition: bool, message: &str) -> Result<()> {
    if condition {
        Ok(())
    } else {
        Err(Error::invalid_response(message))
    }
}
fn observation(check: &str) {
    println!(
        "{}",
        json!({"check":check,"result":"passed","evidence":"packaged_sdk_real_native_producer"})
    );
}
async fn commit(client: &TerraScaleClient, operation: &PreparedTransaction) -> Result<Receipt> {
    match client.transactions().commit(operation).await? {
        CommitOutcome::Committed(receipt) => Ok(receipt),
        CommitOutcome::Uncertain(_) => Err(Error::new(
            ErrorKind::Unavailable,
            "qualification_uncertain",
            "Qualification cannot claim success for an uncertain commit.",
        )),
    }
}
async fn point(client: &TerraScaleClient, fixture: &Fixture) -> Result<Point> {
    client
        .documents()
        .get(&fixture.scope, fixture.collection, fixture.document)
        .await
}
fn version(point: &Point) -> Result<Digest> {
    point
        .state_version
        .ok_or_else(|| Error::invalid_response("Expected unique live/deleted state token."))
}

async fn seed(client: &TerraScaleClient, fixture: &Fixture) -> Result<PreparedTransaction> {
    check(
        point(client, fixture).await?.state == PointState::Absent,
        "Qualification document must be fresh and have no retained state.",
    )?;
    let operation = fixture.prepared(vec![fixture.write(
        fixture.document,
        Condition::Absent {},
        vec![],
        WriteValue::Live {
            fields: fixture.fields(1)?,
        },
    )])?;
    commit(client, &operation).await?;
    observation("native_seed");
    Ok(operation)
}

async fn verify(client: &TerraScaleClient, fixture: &Fixture) -> Result<()> {
    let got = point(client, fixture).await?;
    check(
        got.live_fields()? == Some(&fixture.fields(1)?),
        "Native typed fields differ.",
    )?;
    observation("native_point_values");
    Ok(())
}

async fn cleanup(client: &TerraScaleClient, fixture: &Fixture) -> Result<()> {
    let got = point(client, fixture).await?;
    if got.state == PointState::Absent || got.state == PointState::Deleted {
        observation("native_cleanup");
        return Ok(());
    }
    let operation = fixture.prepared(vec![fixture.write(
        fixture.document,
        Condition::Version {
            version: version(&got)?,
        },
        got.context,
        WriteValue::Tombstone {},
    )])?;
    commit(client, &operation).await?;
    check(
        point(client, fixture).await?.state == PointState::Deleted,
        "Tombstone must remain deleted, not absent.",
    )?;
    observation("native_cleanup");
    Ok(())
}

async fn exercise(client: &TerraScaleClient, fixture: &Fixture) -> Result<()> {
    // Admit all local prerequisites before any producer request or mutation.
    let denied_key = fixture
        .deny_key
        .as_deref()
        .filter(|key| !key.is_empty() && *key != fixture.key)
        .ok_or_else(|| {
            Error::new(
                ErrorKind::Validation,
                "missing_deny_fixture",
                "Exercise requires a distinct deny API key.",
            )
        })?;
    let denied = fixture.client(denied_key)?;
    // Get a producer snapshot cursor before the initial write; poll then proves the actual log path.
    let snapshot_input = SnapshotRequest::new(fixture.predicate(1), 128)?;
    let snapshot = client
        .subscriptions()
        .snapshot(&fixture.scope, fixture.collection, &snapshot_input)
        .await?;
    let mut view = SubscriptionView::default();
    view.apply_snapshot(
        &fixture.scope,
        fixture.collection,
        &snapshot_input,
        &snapshot,
    )?;
    let cursor = snapshot.poll_cursor.ok_or_else(|| {
        Error::invalid_response("Qualification fixture needs a bounded completed snapshot.")
    })?;
    let operation = seed(client, fixture).await?;
    verify(client, fixture).await?;
    let status = client.transactions().status(&operation).await?;
    check(
        status.state == TransactionState::Committed,
        "Original commit status is not committed.",
    )?;
    let retry = commit(client, &operation).await?;
    let Some(StatusOutcome::Committed(original)) = status.outcome else {
        return Err(Error::invalid_response("Status omitted original receipt."));
    };
    check(
        retry == original.receipt,
        "Exact retry changed original durable receipt.",
    )?;
    observation("native_exact_retry_and_status");

    let changed = PreparedTransaction::new(
        fixture.scope,
        operation.transaction_id().clone(),
        Authority::Leaderless {},
        vec![fixture.write(
            fixture.document,
            Condition::Absent {},
            vec![],
            WriteValue::Live {
                fields: fixture.fields(2)?,
            },
        )],
    )?;
    let error = client
        .transactions()
        .commit(&changed)
        .await
        .expect_err("changed effect identity must be rejected");
    check(
        error.status == Some(409) && error.code == "idempotency_conflict",
        "Changed effect reused identity without idempotency conflict.",
    )?;
    observation("native_changed_effect_rejected");

    let current = point(client, fixture).await?;
    let other_document = DocumentId::new();
    let atomic = fixture.prepared(vec![
        fixture.write(
            fixture.document,
            Condition::Version {
                version: Digest::from_bytes([255; 32]),
            },
            current.context.clone(),
            WriteValue::Live {
                fields: fixture.fields(2)?,
            },
        ),
        fixture.write(
            other_document,
            Condition::Absent {},
            vec![],
            WriteValue::Live {
                fields: fixture.fields(2)?,
            },
        ),
    ])?;
    let error = client
        .transactions()
        .commit(&atomic)
        .await
        .expect_err("stale group must be rejected");
    check(
        error.status == Some(412) && error.code == "precondition_failed",
        "Stale condition did not reject atomically.",
    )?;
    check(
        client
            .documents()
            .get(&fixture.scope, fixture.collection, other_document)
            .await?
            .state
            == PointState::Absent,
        "Failed atomic group published its peer write.",
    )?;
    check(
        client
            .transactions()
            .status(&atomic)
            .await?
            .definitively_rejected(),
        "Rejected group did not retain definitive status.",
    )?;
    observation("native_atomic_stale_group_and_rejected_status");

    let poll_input = PollRequest::new(fixture.predicate(1), cursor)?;
    let poll = client
        .subscriptions()
        .poll(&fixture.scope, fixture.collection, &poll_input)
        .await?;
    check(
        poll.groups
            .iter()
            .flat_map(|group| &group.effects)
            .any(|effect| effect.document == fixture.document && effect.point.is_some()),
        "Native subscription omitted committed matching effect.",
    )?;
    view.apply_poll(&fixture.scope, fixture.collection, &poll_input, &poll)?;
    check(
        view.rows().contains_key(&fixture.document),
        "Native subscription view did not apply complete group.",
    )?;
    observation("native_subscription_poll_groups");

    let query = Query::new(fixture.predicate(1), 1)?;
    let page = client
        .documents()
        .query(&fixture.scope, fixture.collection, &query)
        .await?;
    check(
        page.rows.iter().any(|row| row.document == fixture.document),
        "Actual native scalar query omitted document.",
    )?;
    observation("native_query");

    let update = fixture.prepared(vec![fixture.write(
        fixture.document,
        Condition::Version {
            version: version(&current)?,
        },
        current.context,
        WriteValue::Live {
            fields: fixture.fields(2)?,
        },
    )])?;
    commit(client, &update).await?;
    let removed = client
        .subscriptions()
        .poll(
            &fixture.scope,
            fixture.collection,
            &PollRequest::new(fixture.predicate(1), poll.poll_cursor)?,
        )
        .await?;
    check(
        removed
            .groups
            .iter()
            .flat_map(|group| &group.effects)
            .any(|effect| effect.document == fixture.document && effect.point.is_none()),
        "Subscription did not remove a document that stopped matching.",
    )?;
    observation("native_subscription_removal");

    let error = denied
        .documents()
        .get(&fixture.scope, fixture.collection, fixture.document)
        .await
        .expect_err("denied credentials must fail");
    check(
        matches!(error.status, Some(401 | 403)),
        "Denied native credential did not fail authorization.",
    )?;
    let mut other_scope = fixture.scope;
    other_scope.tenant = TenantId::new();
    let error = client
        .documents()
        .get(&other_scope, fixture.collection, fixture.document)
        .await
        .expect_err("cross tenant must fail");
    check(
        matches!(error.status, Some(401 | 403 | 404)),
        "Cross-tenant scope was accepted.",
    )?;
    observation("native_scope_and_credential_isolation");
    cleanup(client, fixture).await
}

#[tokio::main]
async fn main() -> ExitCode {
    let phase = env::args().nth(1).unwrap_or_else(|| "exercise".into());
    let mut fixture_identity = None;
    let result = async {
        let fixture = Fixture::load()?;
        fixture_identity = Some(json!({"tenant":fixture.scope.tenant,"database":fixture.scope.database,"shard":fixture.scope.shard,"collection":fixture.collection,"document":fixture.document}));
        let client = fixture.client(&fixture.key)?;
        match phase.as_str() {
            "atlas-point" => {
                let client = fixture.atlas_client()?;
                let monotonic_start = std::time::Instant::now();
                let invalid_clock = || {
                    Error::new(ErrorKind::Unavailable, "invalid_clock", "Invalid qualification clock.")
                };
                let start = SystemTime::now()
                    .duration_since(UNIX_EPOCH)
                    .map_err(|_| invalid_clock())?;
                let resolved = client.discovery()
                    .resolve(&fixture.scope, NativeRequirement::Point).await?;
                check(
                    resolved.endpoint.url == fixture.origin,
                    "Atlas selected a different origin from the actual independently admitted fixture.",
                )?;
                let result = point(&client, &fixture).await?;
                let end = SystemTime::now()
                    .duration_since(UNIX_EPOCH)
                    .map_err(|_| invalid_clock())?;
                check(
                    monotonic_start.elapsed() < std::time::Duration::from_secs(resolved.metadata.refresh_seconds)
                        && end.as_millis() < u128::from(resolved.metadata.expires_at) * 1000,
                    "Atlas point phase exceeded the observed conservative lease.",
                )?;
                println!("{}", json!({
                    "check": "atlas_authenticated_resolution_native_point",
                    "result": "passed",
                    "evidence": "separate_successor_packaged_sdk_actual_receiver_and_native",
                    "receiver": ATLAS_BASE_URL,
                    "scope": {
                        "tenant_id": fixture.scope.tenant,
                        "database_id": fixture.scope.database,
                        "shard_id": fixture.scope.shard,
                    },
                    "authority": {
                        "deployment_id": resolved.metadata.authority.deployment_id,
                        "cell": resolved.metadata.authority.cell,
                        "publisher_id": resolved.metadata.authority.publisher_id,
                        "publisher_generation": resolved.metadata.authority.publisher_generation,
                    },
                    "revision": resolved.metadata.revision,
                    "published_at": resolved.metadata.published_at,
                    "expires_at": resolved.metadata.expires_at,
                    "refresh_seconds": resolved.metadata.refresh_seconds,
                    "region": resolved.endpoint.region,
                    "origin": resolved.endpoint.url,
                    "catalog_digest": resolved.endpoint.catalog_digest,
                    "point_state": result.state,
                    "request_start_unix_millis": start.as_millis().to_string(),
                    "point_end_unix_millis": end.as_millis().to_string(),
                    "clock_health_qualification": "requires_independent_measured_bounds_and_provenance",
                }));
                Ok(())
            }
            "tls-check" => {
                if !client
                    .config()
                    .api_base_url()
                    .is_some_and(|origin| origin.starts_with("https://"))
                {
                    return Err(Error::new(
                        ErrorKind::Validation,
                        "invalid_fixture_origin",
                        "TLS qualification requires HTTPS.",
                    ));
                }
                point(&client, &fixture).await?;
                observation("native_tls_verified");
                Ok(())
            }
            "seed" => seed(&client, &fixture).await.map(|_| ()),
            "verify" => verify(&client, &fixture).await,
            "exercise" => exercise(&client, &fixture).await,
            "cleanup" => cleanup(&client, &fixture).await,
            _ => Err(Error::new(
                ErrorKind::Validation,
                "invalid_phase",
                "Expected atlas-point, tls-check, seed, verify, exercise or cleanup.",
            )),
        }
    }
    .await;
    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!(
                "{}",
                json!({"phase":phase,"result":"failed","code":error.code,"status":error.status,"fixture":fixture_identity})
            );
            ExitCode::FAILURE
        }
    }
}
