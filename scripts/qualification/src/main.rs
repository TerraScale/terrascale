use serde_json::json;
use std::{
    env,
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

