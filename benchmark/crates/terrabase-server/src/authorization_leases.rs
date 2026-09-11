//! Signed tenant authority leases. Control polling is per tenant; delivery is local only.
use serde::{Deserialize, Serialize};
use std::{collections::BTreeMap, path::PathBuf, sync::RwLock};
use terrabase::regions::RegionCode;

const MAX_SAFE: u64 = 9_007_199_254_740_991;
const MAX_TENANTS: usize = 4096;
const RETAIN_SECONDS: u64 = 1200;

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq, utoipa::ToSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(crate) struct Lease {
    pub version: u8,
    pub tenant_id: String,
    pub epoch: String,
    pub feature_epoch: String,
    pub audience: String,
    pub region: String,
    pub issued_at: u64,
    pub valid_until: u64,
    pub key_id: String,
    pub signature: String,
}
impl Lease {
    pub fn scope(&self) -> String {
        format!("authz-{}-{}-{}", self.tenant_id, self.epoch, self.feature_epoch)
    }
    fn canonical(&self) -> String {
        format!(
            "terrascale-authorization-lease-v1\n{}\n{}\n{}\n{}\n{}\n{}\n{}\n{}",
            self.key_id,
            self.tenant_id,
            self.epoch,
            self.feature_epoch,
            self.audience,
            self.region,
            self.issued_at,
            self.valid_until
        )
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Key {
    key_id: String,
    key: String,
    not_before: u64,
    not_after: u64,
}
impl Drop for Key {
    fn drop(&mut self) {
        use zeroize::Zeroize as _;
        self.key.zeroize();
    }
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Keys {
    keys: Vec<Key>,
}

pub(crate) struct AuthorityScope<'a> {
    pub tenant: &'a str,
    pub epoch: u64,
    pub feature_epoch: u64,
}
pub(crate) fn tenant_id(value: &str) -> bool {
    value.parse::<terrabase::identity::TenantUlid>().is_ok_and(|id| id.to_string() == value)
}
fn decimal(value: &str) -> Option<u64> {
    let number = value.parse::<u64>().ok()?;
    (number.to_string() == value).then_some(number)
}
pub(crate) fn parse_scope(scope: &str) -> Option<AuthorityScope<'_>> {
    let mut parts = scope.strip_prefix("authz-")?.split('-');
    let tenant = parts.next()?;
    let epoch = decimal(parts.next()?)?;
    let feature_epoch = decimal(parts.next()?)?;
    (parts.next().is_none() && tenant_id(tenant)).then_some(AuthorityScope {
        tenant,
        epoch,
        feature_epoch,
    })
}
/// Database scope hashes remain independent of tenant authorization epochs.
pub(crate) fn valid_database_scope(scope: &str) -> bool {
    scope.len() == 70
        && scope.starts_with("scope-")
        && scope.as_bytes()[6..].iter().all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(c))
}
fn valid_key_id(id: &str) -> bool {
    !id.is_empty()
        && id.len() <= 32
        && id.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'-')
}
fn lower_hex(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

struct Cached {
    lease: Lease,
    retain_until: u64,
}
#[derive(Default)]
struct State {
    tenants: BTreeMap<String, Cached>,
    feature_floor: u64,
}
#[derive(Default)]
pub(crate) struct AuthorizationLeases {
    config: Option<(String, RegionCode, PathBuf)>,
    state: RwLock<State>,
    #[cfg(test)]
    test_keys: Option<Vec<u8>>,
}
impl AuthorizationLeases {
    pub fn from_file(audience: String, region: RegionCode, path: PathBuf) -> Result<Self, ()> {
        if !path.is_absolute() {
            return Err(());
        }
        let registry = Self { config: Some((audience, region, path)), ..Self::default() };
        registry.keys()?;
        Ok(registry)
    }
    pub fn region(&self) -> Result<&str, ()> {
        self.config.as_ref().map(|(_, region, _)| region.as_str()).ok_or(())
    }
    fn keys(&self) -> Result<Keys, ()> {
        #[cfg(test)]
        if let Some(bytes) = &self.test_keys {
            return serde_json::from_slice(bytes).map_err(|_| ());
        }
        let (_, _, path) = self.config.as_ref().ok_or(())?;
        let bytes = zeroize::Zeroizing::new(crate::runtime::read_secret_key(path).map_err(|_| ())?);
        let keys: Keys = serde_json::from_slice(&bytes).map_err(|_| ())?;
        if keys.keys.is_empty() || keys.keys.len() > 2 {
            return Err(());
        }
        for (i, key) in keys.keys.iter().enumerate() {
            if !valid_key_id(&key.key_id)
                || !lower_hex(&key.key)
                || key.not_before >= key.not_after
                || key.not_after > MAX_SAFE
                || keys.keys[..i]
                    .iter()
                    .any(|prior| prior.key_id == key.key_id || prior.key == key.key)
            {
                return Err(());
            }
        }
        Ok(keys)
    }
    fn verify(&self, lease: &Lease, now: u64) -> Result<(), ()> {
        let (audience, region, _) = self.config.as_ref().ok_or(())?;
        if lease.version != 1
            || !tenant_id(&lease.tenant_id)
            || decimal(&lease.epoch).is_none()
            || decimal(&lease.feature_epoch).is_none()
            || lease.audience != *audience
            || lease.audience.len() > 256
            || !lease.audience.is_ascii()
            || lease.audience.bytes().any(|b| b.is_ascii_whitespace())
            || lease.region != region.as_str()
            || !matches!(lease.region.as_str(), "GRU" | "SFO" | "FRA")
            || lease.issued_at > now
            || lease.valid_until <= now
            || lease.valid_until > lease.issued_at.saturating_add(30)
            || lease.valid_until > MAX_SAFE
            || !valid_key_id(&lease.key_id)
            || !lower_hex(&lease.signature)
            || serde_json::to_vec(lease).map_err(|_| ())?.len() > 1024
        {
            return Err(());
        }
        let keys = self.keys()?;
        let key = keys
            .keys
            .iter()
            .find(|key| {
                key.key_id == lease.key_id
                    && key.not_before <= lease.issued_at
                    && key.not_before <= now
                    && key.not_after > now
                    && key.not_after >= lease.valid_until
            })
            .ok_or(())?;
        let bytes = zeroize::Zeroizing::new(hex::decode(&key.key).map_err(|_| ())?);
        let expected =
            crate::view_capability::hmac_sha256_hex(&bytes, lease.canonical().as_bytes());
        if expected.bytes().zip(lease.signature.bytes()).fold(0_u8, |diff, (a, b)| diff | (a ^ b))
            != 0
        {
            return Err(());
        }
        Ok(())
    }
    /// Only successful authenticated admissions register tenants; polling cannot extend retention.
    pub fn install(&self, lease: Lease, now: u64, register: bool) -> Result<(), ()> {
        self.verify(&lease, now)?;
        let epoch = decimal(&lease.epoch).ok_or(())?;
        let feature = decimal(&lease.feature_epoch).ok_or(())?;
        let mut state = self.state.write().map_err(|_| ())?;
        state.tenants.retain(|_, cached| cached.retain_until > now);
        if feature < state.feature_floor {
            return Err(());
        }
        let retention = if let Some(old) = state.tenants.get(&lease.tenant_id) {
            if epoch < decimal(&old.lease.epoch).ok_or(())?
                || feature < decimal(&old.lease.feature_epoch).ok_or(())?
                || lease.issued_at < old.lease.issued_at
                || lease.valid_until < old.lease.valid_until
            {
                return Err(());
            }
            old.retain_until
        } else {
            if !register || state.tenants.len() >= MAX_TENANTS {
                return Err(());
            }
            now
        };
        state.feature_floor = feature;
        state.tenants.insert(
            lease.tenant_id.clone(),
            Cached {
                lease,
                retain_until: if register {
                    retention.max(now.saturating_add(RETAIN_SECONDS))
                } else {
                    retention
                },
            },
        );
        Ok(())
    }
    pub fn admits(&self, scope: &str, now: u64) -> bool {
        let Some(scope) = parse_scope(scope) else { return false };
        self.state.read().is_ok_and(|state| {
            state.tenants.get(scope.tenant).is_some_and(|cached| {
                cached.retain_until > now
                    && cached.lease.valid_until > now
                    && decimal(&cached.lease.epoch) == Some(scope.epoch)
                    && decimal(&cached.lease.feature_epoch) == Some(scope.feature_epoch)
                    && scope.feature_epoch == state.feature_floor
            })
        })
    }
    pub fn active_tenants(&self, now: u64) -> Vec<String> {
        let Ok(mut state) = self.state.write() else { return vec![] };
        state.tenants.retain(|_, cached| cached.retain_until > now);
        state.tenants.keys().cloned().collect()
    }
    pub fn lease(&self, tenant: &str, now: u64) -> Option<Lease> {
        let state = self.state.read().ok()?;
        let cached = state.tenants.get(tenant)?;
        (cached.retain_until > now
            && cached.lease.issued_at <= now
            && cached.lease.valid_until > now
            && decimal(&cached.lease.feature_epoch) == Some(state.feature_floor))
        .then(|| cached.lease.clone())
    }
    #[cfg(test)]
    pub fn conformance(audience: &str, region: RegionCode) -> Self {
        Self { config: Some((audience.into(), region, PathBuf::new())), test_keys: Some(serde_json::to_vec(&serde_json::json!({"keys":[{"keyId":"lease-vector","key":"11".repeat(32),"notBefore":0,"notAfter":MAX_SAFE}]})).unwrap()), ..Self::default() }
    }
}

#[cfg(test)]
pub(crate) fn signed_test_lease(tenant: &str, epoch: u64, feature: u64, now: u64) -> Lease {
    let mut lease = Lease {
        version: 1,
        tenant_id: tenant.into(),
        epoch: epoch.to_string(),
        feature_epoch: feature.to_string(),
        audience: "https://api.example".into(),
        region: "GRU".into(),
        issued_at: now,
        valid_until: now + 30,
        key_id: "lease-vector".into(),
        signature: String::new(),
    };
    lease.signature =
        crate::view_capability::hmac_sha256_hex(&[0x11; 32], lease.canonical().as_bytes());
    lease
}

pub(crate) fn is_public_lease_path(path: &str) -> bool {
    path.strip_prefix("/api/v1/authorization/leases/").is_some_and(tenant_id)
}

/// Returns a previously authenticated producer lease, with its original absolute expiry.
/// This route never fetches identity state or renews a lease on a reader's behalf.
#[utoipa::path(get, path = "/api/v1/authorization/leases/{tenant}", params(("tenant"=String,Path)), responses((status=200,body=Lease),(status=503,description="No current authenticated tenant lease")))]
pub async fn authorization_lease(
    axum::extract::State(state): axum::extract::State<std::sync::Arc<crate::api::ApiState>>,
    axum::extract::Path(tenant): axum::extract::Path<String>,
) -> axum::response::Response {
    use axum::response::IntoResponse as _;
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(u64::MAX, |v| v.as_secs());
    let lease = tenant_id(&tenant)
        .then(|| state.delegated_identity.as_ref()?.authorization.lease(&tenant, now))
        .flatten();
    let Some(lease) = lease else {
        return crate::api::ApiError::control_unavailable().into_response();
    };
    let remaining = lease.valid_until.saturating_sub(now).min(10);
    let mut response = axum::Json(lease).into_response();
    response.headers_mut().insert(
        "cache-control",
        format!("public, max-age={remaining}, must-revalidate")
            .parse()
            .expect("bounded cache header"),
    );
    response
        .headers_mut()
        .insert("access-control-allow-origin", axum::http::HeaderValue::from_static("*"));
    response
}

#[cfg(test)]
mod tests {
    use super::*;
    const TENANT: &str = "01J00000000000000000000002";
    const OTHER: &str = "01J00000000000000000000003";
    fn registry() -> AuthorizationLeases {
        AuthorizationLeases::conformance("https://api.example", RegionCode::GRU)
    }
    #[test]
    fn signed_expiry_epoch_floor_and_global_feature_never_resurrect() {
        let registry = registry();
        let first = signed_test_lease(TENANT, 1, 1, 100);
        assert!(!registry.admits(&first.scope(), 100));
        registry.install(first.clone(), 100, true).unwrap();
        assert!(registry.admits(&first.scope(), 129));
        assert!(!registry.admits(&first.scope(), 130));
        assert!(registry.install(first.clone(), 130, false).is_err());
        assert!(registry.lease(TENANT, 130).is_none());
        registry.install(signed_test_lease(TENANT, 2, 1, 101), 101, false).unwrap();
        assert!(!registry.admits(&first.scope(), 101));
        assert!(registry.install(first, 101, false).is_err());
        let second = signed_test_lease(OTHER, 1, 2, 102);
        registry.install(second.clone(), 102, true).unwrap();
        assert!(!registry.admits(&signed_test_lease(TENANT, 2, 1, 101).scope(), 102));
        assert!(registry.lease(TENANT, 102).is_none());
        assert!(registry.admits(&second.scope(), 102));
    }
    #[test]
    fn feature_floor_advances_across_tenants_without_per_tenant_downgrade() {
        let registry = registry();
        let tenant_a = signed_test_lease(TENANT, 1, 7, 100);
        let tenant_b = signed_test_lease(OTHER, 1, 8, 101);
        registry.install(tenant_a.clone(), 100, true).unwrap();
        registry.install(tenant_b.clone(), 101, true).unwrap();

        // The feature floor is shared by this member: a later lease for tenant A
        // must carry the floor established by tenant B, even though A's own
        // cached lease still has the lower feature epoch.
        assert!(registry.install(signed_test_lease(TENANT, 2, 7, 102), 102, false).is_err());
        assert!(!registry.admits(&tenant_a.scope(), 102));
        assert!(registry.admits(&tenant_b.scope(), 102));
        let tenant_a_at_floor = signed_test_lease(TENANT, 2, 8, 102);
        registry.install(tenant_a_at_floor.clone(), 102, false).unwrap();
        assert!(registry.admits(&tenant_a_at_floor.scope(), 102));
    }
    #[test]
    fn malformed_scope_signature_tenant_audience_region_and_times_fail_closed() {
        for change in 0..8 {
            let registry = registry();
            let mut lease = signed_test_lease(TENANT, 1, 1, 100);
            match change {
                0 => lease.tenant_id = OTHER.into(),
                1 => lease.audience = "https://evil.example".into(),
                2 => lease.region = "FRA".into(),
                3 => lease.epoch = "01".into(),
                4 => lease.feature_epoch = "18446744073709551616".into(),
                5 => lease.issued_at = 101,
                6 => lease.valid_until = 131,
                _ => lease.signature = "00".repeat(32),
            }
            assert!(registry.install(lease, 100, true).is_err(), "case {change}");
            assert!(registry.active_tenants(100).is_empty());
        }
        for scope in [
            "authz-01J00000000000000000000002-01-1",
            "authz-01j00000000000000000000002-1-1",
            "authz-01J00000000000000000000002-1-1-extra",
        ] {
            assert!(parse_scope(scope).is_none());
        }
    }
    #[test]
    fn polling_retention_is_per_tenant_and_reader_lookups_do_not_renew() {
        let registry = registry();
        let lease = signed_test_lease(TENANT, 1, 1, 100);
        registry.install(lease.clone(), 100, true).unwrap();
        for _ in 0..10_000 {
            assert!(registry.admits(&lease.scope(), 101));
        }
        assert_eq!(registry.active_tenants(101), [TENANT]);
        registry.install(signed_test_lease(TENANT, 1, 1, 120), 120, false).unwrap();
        assert_eq!(registry.lease(TENANT, 121).unwrap().valid_until, 150);
        assert!(registry.active_tenants(1300).is_empty());
        assert!(registry.install(signed_test_lease(OTHER, 1, 1, 120), 120, false).is_err());
    }
    #[test]
    fn private_keyring_rotation_never_falls_back_to_removed_material() {
        use std::os::unix::fs::PermissionsExt as _;
        let directory = std::env::temp_dir()
            .join(format!("authorization-keys-{}", terrabase::identity::DatabaseUlid::generate()));
        std::fs::create_dir(&directory).unwrap();
        std::fs::set_permissions(&directory, std::fs::Permissions::from_mode(0o700)).unwrap();
        let path = directory.join("keys.json");
        let write = |key: &str| {
            std::fs::write(&path, serde_json::to_vec(&serde_json::json!({"keys":[{"keyId":"lease-vector","key":key,"notBefore":0,"notAfter":1000}]})).unwrap()).unwrap();
            std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o400)).unwrap();
        };
        write(&"11".repeat(32));
        let registry = AuthorizationLeases::from_file(
            "https://api.example".into(),
            RegionCode::GRU,
            path.clone(),
        )
        .unwrap();
        let lease = signed_test_lease(TENANT, 1, 1, 100);
        registry.install(lease.clone(), 100, true).unwrap();
        std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o600)).unwrap();
        write(&"22".repeat(32));
        assert!(registry.install(signed_test_lease(TENANT, 1, 1, 101), 101, false).is_err());
        assert_eq!(registry.lease(TENANT, 101).unwrap().valid_until, 130);
        assert!(!registry.admits(&lease.scope(), 130));
        std::fs::remove_dir_all(directory).unwrap();
    }
    #[test]
    fn producer_vector_has_identical_canonical_bytes() {
        let value: serde_json::Value =
            serde_json::from_str(include_str!("tenant-authority-capability-vector.json")).unwrap();
        let lease: Lease = serde_json::from_value(value["lease"].clone()).unwrap();
        assert_eq!(
            lease.signature,
            "edf5da55da53698563b0633428d1bab21e92ba4d35df1cda43806174d562ac23"
        );
        registry().install(lease, 1000, true).unwrap();
    }
}
