//! Hrana HTTP envelopes. SQL errors remain results inside an HTTP 200 response.
use super::{
    client::{Req, Resp},
    errors::from_project_response,
    ids::{check_name, valid_entity_id},
    Database, Error, Result,
};
use reqwest::{Method, StatusCode};
use serde::{Deserialize, Serialize};
use serde_json::value::RawValue;
use std::fmt;

#[derive(Debug, Clone, Copy)]
pub enum HranaVersion {
    V2,
    V3,
}
impl HranaVersion {
    fn path(self) -> &'static str {
        match self {
            Self::V2 => "v2",
            Self::V3 => "v3",
        }
    }
}
/// Opaque results retain exact JSON and the Hrana snake_case wire contract.
#[derive(Clone, Deserialize)]
pub struct HranaPipelineResponse {
    #[serde(deserialize_with = "Option::deserialize")]
    pub baton: Option<String>,
    #[serde(deserialize_with = "Option::deserialize")]
    pub base_url: Option<String>,
    pub results: Vec<Box<RawValue>>,
}
impl fmt::Debug for HranaPipelineResponse {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("HranaPipelineResponse")
            .field("results", &self.results.len())
            .finish_non_exhaustive()
    }
}
/// A complete bounded cursor response. No entries are returned on a truncated body.
#[derive(Clone)]
pub struct HranaCursor {
    pub baton: Option<String>,
    pub base_url: Option<String>,
    pub entries: Vec<Box<RawValue>>,
}
impl fmt::Debug for HranaCursor {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("HranaCursor")
            .field("entries", &self.entries.len())
            .finish_non_exhaustive()
    }
}
fn private_error(error: Error) -> Error {
    if matches!(error, Error::TokenExchange { .. } | Error::InvalidInput(_)) {
        return error;
    }
    let Some(status) = error.status() else {
        return Error::Transport("Hrana request failed".into());
    };
    // Hrana request refusals use a flat body, unlike ordinary project errors.
    let flat: Option<serde_json::Value> = error
        .raw_body()
        .and_then(|b| serde_json::from_slice(b).ok());
    let code = flat
        .as_ref()
        .and_then(|v| v.get("code"))
        .and_then(|v| v.as_str())
        .or_else(|| error.code())
        .filter(|s| {
            matches!(
                *s,
                "STREAM_EXPIRED"
                    | "PROTOCOL_ERROR"
                    | "DATABASE_STREAMS_EXHAUSTED"
                    | "ORGANIZATION_STREAMS_EXHAUSTED"
                    | "AUTH_REQUIRED"
                    | "AUTH_EXPIRED"
                    | "invalid_key"
                    | "forbidden_scope"
                    | "not_found"
                    | "unknown_database"
                    | "capacity"
                    | "unavailable"
                    | "internal_error"
                    | "misdirected_request"
                    | "wrong_region"
            )
        })
        .unwrap_or("hrana_error");
    from_project_response(
        status,
        None,
        None,
        error.request_id().filter(|s| valid_entity_id(s)),
        serde_json::json!({"error":{"code":code}})
            .to_string()
            .as_bytes(),
    )
}
impl Database {
    async fn hrana_request(
        &self,
        version: HranaVersion,
        operation: &str,
        body: &impl Serialize,
    ) -> Result<Resp> {
        check_name("database", self.name())?;
        let req = Req::new(
            Method::POST,
            vec![
                "v1",
                "databases",
                self.name(),
                "libsql",
                version.path(),
                operation,
            ],
        )
        .json(body)?;
        if req.body.as_ref().is_some_and(|b| b.len() > 1 << 20) {
            return Err(Error::InvalidInput("Hrana request exceeds 1 MiB".into()));
        }
        self.client().send_project(req).await.map_err(private_error)
    }
    /// Raw Hrana envelope: caller supplies snake_case protocol fields and baton.
    /// Does not replay an uncertain POST or follow the returned base_url.
    pub async fn libsql_pipeline(
        &self,
        version: HranaVersion,
        body: &impl Serialize,
    ) -> Result<HranaPipelineResponse> {
        self.hrana_request(version, "pipeline", body)
            .await?
            .decode_status(&[StatusCode::OK])
            .map_err(private_error)
    }
    /// Hrana v3 only. Buffers within the client's response-size limit.
    pub async fn libsql_cursor(&self, body: &impl Serialize) -> Result<HranaCursor> {
        let response = self.hrana_request(HranaVersion::V3, "cursor", body).await?;
        response
            .check_status(&[StatusCode::OK])
            .map_err(private_error)?;
        let invalid =
            || private_error(response.invalid("invalid or incomplete Hrana cursor".into()));
        if !response.body.ends_with(b"\n") {
            return Err(invalid());
        }
        let mut lines = response.body[..response.body.len() - 1].split(|b| *b == b'\n');
        #[derive(Deserialize)]
        struct Header {
            #[serde(deserialize_with = "Option::deserialize")]
            baton: Option<String>,
            #[serde(deserialize_with = "Option::deserialize")]
            base_url: Option<String>,
        }
        let header: Header =
            serde_json::from_slice(lines.next().ok_or_else(invalid)?).map_err(|_| invalid())?;
        let entries = lines
            .map(|line| serde_json::from_slice::<Box<RawValue>>(line).map_err(|_| invalid()))
            .collect::<Result<Vec<_>>>()?;
        Ok(HranaCursor {
            baton: header.baton,
            base_url: header.base_url,
            entries,
        })
    }
}
