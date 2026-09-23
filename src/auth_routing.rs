//! Atlas auth discovery for direct regional ts-auth requests.

use std::str::FromStr;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use serde::Deserialize;

use crate::error::{Error, ErrorKind, Result};
use crate::regions::RegionCode;
use crate::transport::{HttpTransport, Method, Request};

const PUBLIC_AUTH_BASE: &str = "https://auth.terrascale.tech";
const AUTH_DISCOVERY_PATH: &str = "/api/v1/auth/discovery";
const MAX_BODY_BYTES: usize = 65_536;

#[derive(Debug)]
struct Snapshot {
    endpoints: Vec<String>,
    expires: Instant,
}

/// Brief cache of the ranked, validated auth origins. Locks never cross await.
#[derive(Debug)]
pub(crate) struct AuthRouter {
    atlas_origin: String,
    cached: Mutex<Option<Snapshot>>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct DiscoveryDoc {
    v: u8,
    refresh_seconds: u64,
    endpoints: Vec<AuthEndpoint>,
}

#[derive(Deserialize)]
struct AuthEndpoint {
    region: String,
    url: String,
}

impl AuthRouter {
    pub(crate) fn for_config(
        auth_base: Option<&str>,
        reverse_proxy: bool,
        atlas_base: Option<&str>,
    ) -> Option<Self> {
        (auth_base == Some(PUBLIC_AUTH_BASE) && !reverse_proxy).then(|| Self {
            atlas_origin: atlas_base
                .unwrap_or(crate::discovery::ATLAS_BASE_URL)
                .to_owned(),
            cached: Mutex::new(None),
        })
    }

    pub(crate) async fn endpoints(
        &self,
        transport: &Arc<dyn HttpTransport>,
    ) -> Result<Vec<String>> {
        if let Some(snapshot) = self.cached.lock().ok().and_then(|guard| {
            guard
                .as_ref()
                .filter(|snapshot| snapshot.expires > Instant::now())
                .map(|snapshot| snapshot.endpoints.clone())
        }) {
            return Ok(snapshot);
        }

        let request = Request {
            route_name: "auth.discovery".to_owned(),
            method: Method::Get,
            url: format!("{}{AUTH_DISCOVERY_PATH}", self.atlas_origin),
            headers: vec![("accept".to_owned(), "application/json".to_owned())],
            body: None,
        };
        let response = tokio::time::timeout(Duration::from_secs(3), transport.send(request))
            .await
            .map_err(|_| unavailable("Atlas auth discovery timed out."))??;
        if response.status != 200 {
            return Err(unavailable("Atlas has no healthy authentication endpoint."));
        }
        if response.body.len() > MAX_BODY_BYTES {
            return Err(invalid("Atlas auth discovery response is too large."));
        }
        let doc: DiscoveryDoc = serde_json::from_slice(&response.body)
            .map_err(|_| invalid("Atlas auth discovery response is invalid."))?;
        if doc.v != 1
            || !(1..=3600).contains(&doc.refresh_seconds)
            || !(1..=20).contains(&doc.endpoints.len())
        {
            return Err(invalid("Atlas auth discovery response is invalid."));
        }
        let mut seen = Vec::with_capacity(doc.endpoints.len());
        let mut origins = Vec::with_capacity(doc.endpoints.len());
        for endpoint in doc.endpoints {
            let region = RegionCode::from_str(&endpoint.region)
                .map_err(|()| invalid("Atlas auth endpoint has an invalid region."))?;
            if endpoint.region != region.as_header_str() || seen.contains(&region) {
                return Err(invalid(
                    "Atlas auth endpoint has an invalid or duplicate region.",
                ));
            }
            let url = reqwest::Url::parse(&endpoint.url)
                .map_err(|_| invalid("Atlas auth endpoint URL is invalid."))?;
            if url.scheme() != "https"
                || url.host_str() != Some(&format!("{}.auth.terrascale.tech", region.as_str()))
                || url.port().is_some()
                || !url.username().is_empty()
                || url.password().is_some()
                || url.path() != "/"
                || url.query().is_some()
                || url.fragment().is_some()
            {
                return Err(invalid(
                    "Atlas auth endpoint URL does not match its region.",
                ));
            }
            seen.push(region);
            origins.push(url.origin().ascii_serialization());
        }
        if let Ok(mut guard) = self.cached.lock() {
            *guard = Some(Snapshot {
                endpoints: origins.clone(),
                expires: Instant::now() + Duration::from_secs(doc.refresh_seconds.min(60)),
            });
        }
        Ok(origins)
    }
}

fn invalid(message: &str) -> Error {
    Error::new(
        ErrorKind::InvalidResponse,
        "auth_discovery_invalid",
        message,
    )
}

fn unavailable(message: &str) -> Error {
    Error::new(
        ErrorKind::Unavailable,
        "auth_discovery_unavailable",
        message,
    )
}
