//! SP-initiated SAML Web SSO for a pinned, per-instance IdP connection.

use std::{collections::HashMap, time::SystemTime};

use auth_contracts::{
    AppResult,
    models::{Connection, VerificationToken},
};
use auth_security::tokens;
use axum::{
    Form,
    extract::{Path, Query, State},
    http::{HeaderMap, HeaderValue, StatusCode, header},
    response::{IntoResponse, Redirect, Response},
};
use base64::{Engine, engine::general_purpose::STANDARD};
use quick_xml::{events::Event, name::ResolveResult, reader::NsReader};
use saml_rs::{
    AcsEndpoint, AssertionSignaturePolicy, AudienceValidationPolicy, AuthnRequest,
    AuthnRequestSigningPolicy, BrowserInput, EntityId, FormField, IdpDescriptor,
    MetadataTrustPolicy, PendingAuthnRequest, PendingSnapshot, RelayStateParam, ReplayPolicy,
    ResponseSignaturePolicy, Saml, SamlValidationContext, SpConfig, SpValidationPolicy,
    SsoResponse, StartSso,
    raw::{
        Binding,
        metadata::{Endpoint, IdpMetadataConfig, try_generate_idp_metadata},
    },
};
use subtle::ConstantTimeEq;
use utoipa::ToSchema;

use super::{
    STATE_TTL_SECONDS, connection_error, cookie_value, find_connection, finish_federated_sign_in,
    internal, not_found_page, now, valid_public_https_url,
};
use crate::{AppState, features::users};

const STATE_COOKIE: &str = "tsauth_saml_state";
const STATE_PURPOSE: &str = "saml.state";
const MAX_SAML_RESPONSE_BASE64: usize = 350_000;
const MAX_SAML_RESPONSE_XML: usize = 256 * 1024;

#[derive(ToSchema)]
#[allow(dead_code)]
struct SamlAcsForm {
    #[schema(rename = "SAMLResponse")]
    saml_response: String,
    #[schema(rename = "RelayState")]
    relay_state: String,
}

/// Construct pinned IdP metadata from connection configuration; no network metadata is trusted.
pub(super) fn idp_descriptor(connection: &Connection) -> Result<IdpDescriptor, saml_rs::SamlError> {
    let entity_id = connection
        .idp_entity_id
        .as_deref()
        .ok_or_else(|| saml_rs::SamlError::Invalid("missing IdP entity ID".into()))?;
    let endpoint = connection
        .sso_redirect_url
        .as_deref()
        .ok_or_else(|| saml_rs::SamlError::Invalid("missing IdP SSO endpoint".into()))?;
    let certificate = connection
        .x509_certificate_pem
        .as_deref()
        .ok_or_else(|| saml_rs::SamlError::Invalid("missing IdP signing certificate".into()))?;
    if valid_public_https_url(endpoint).is_none()
        || validate_idp_certificate_pem(certificate).is_err()
    {
        return Err(saml_rs::SamlError::Invalid(
            "invalid IdP endpoint or certificate".into(),
        ));
    }
    let metadata = try_generate_idp_metadata(&IdpMetadataConfig {
        entity_id: entity_id.to_owned(),
        signing_certs: vec![certificate.to_owned()],
        single_sign_on_service: vec![Endpoint::new(Binding::Redirect, endpoint)],
        ..IdpMetadataConfig::default()
    })?;
    IdpDescriptor::from_metadata_xml_for(
        EntityId::try_new(entity_id)?,
        &metadata,
        MetadataTrustPolicy::UnsignedForCompatibility,
    )
}

/// Validate the exact PEM form and parse it with the same SAML verification-key loader used at runtime.
pub(crate) fn validate_idp_certificate_pem(pem: &str) -> Result<(), saml_rs::SamlError> {
    let pem = pem.trim();
    const BEGIN: &str = "-----BEGIN CERTIFICATE-----";
    const END: &str = "-----END CERTIFICATE-----";
    if !pem.starts_with(BEGIN)
        || !pem.ends_with(END)
        || pem.matches(BEGIN).count() != 1
        || pem.matches(END).count() != 1
    {
        return Err(saml_rs::SamlError::Invalid(
            "expected one PEM certificate".into(),
        ));
    }
    let body = &pem[BEGIN.len()..pem.len() - END.len()];
    let encoded: String = body.split_ascii_whitespace().collect();
    if encoded.is_empty() || STANDARD.decode(&encoded).is_err() {
        return Err(saml_rs::SamlError::Invalid(
            "invalid PEM certificate body".into(),
        ));
    }
    saml_rs::crypto::keys::load_certificate(pem).map(|_| ())
}

fn acs_path(slug: &str, connection_id: &str) -> String {
    format!("/{slug}/connections/{connection_id}/saml/acs")
}

fn acs_url(state: &AppState, slug: &str, connection_id: &str) -> String {
    format!(
        "{}{}",
        state.options.public_origin.trim_end_matches('/'),
        acs_path(slug, connection_id)
    )
}

fn service_provider(
    state: &AppState,
    connection: &Connection,
    slug: &str,
) -> Result<Saml<saml_rs::Sp>, saml_rs::SamlError> {
    if valid_public_https_url(&state.options.public_origin).is_none() {
        return Err(saml_rs::SamlError::Invalid(
            "SAML requires an HTTPS public origin".into(),
        ));
    }
    let acs = acs_url(state, slug, &connection.id);
    // Without a metadata route, the registered ACS URL is the stable default entity ID.
    let entity_id = connection.sp_entity_id.as_deref().unwrap_or(&acs);
    let config = SpConfig::builder(EntityId::try_new(entity_id)?)
        .acs_endpoint(AcsEndpoint::post(&acs)?)
        .validation(sp_validation_policy())
        .build()?;
    Saml::sp(config)
}

fn sp_validation_policy() -> SpValidationPolicy {
    let mut policy = SpValidationPolicy::strict();
    policy.authn_requests = AuthnRequestSigningPolicy::DoNotSignForCompatibility;
    policy.assertions = AssertionSignaturePolicy::AllowUnsignedForCompatibility;
    policy.responses = ResponseSignaturePolicy::RequireForEncryptedCbc;
    policy.audience = AudienceValidationPolicy::Validate;
    policy
}

#[utoipa::path(
    get,
    path = "/ui/{slug}/connections/{connection_id}/saml/login",
    params(("slug" = String, Path), ("connection_id" = String, Path), ("next" = Option<String>, Query)),
    responses((status = 303, description = "Redirect to the configured IdP"), (status = 404, description = "Connection not found")),
    tag = "Connections"
)]
pub async fn saml_login(
    State(state): State<AppState>,
    Path((slug, connection_id)): Path<(String, String)>,
    Query(query): Query<HashMap<String, String>>,
) -> AppResult<Response> {
    let Some(instance) = state
        .store
        .find_instance_by_slug(&slug)
        .await
        .map_err(internal)?
    else {
        return Ok(not_found_page());
    };
    let Some(connection) =
        find_connection(&state.store, &instance.id, &connection_id, "saml").await?
    else {
        return Ok(not_found_page());
    };
    let (Ok(idp), Ok(sp)) = (
        idp_descriptor(&connection),
        service_provider(&state, &connection, &slug),
    ) else {
        return Ok(connection_error(
            &slug,
            "This SAML connection is incomplete.",
            StatusCode::BAD_REQUEST,
        ));
    };
    let relay = tokens::random_token(40);
    let Ok(relay_param) = RelayStateParam::try_from_option(Some(relay.clone())) else {
        return Err(auth_contracts::AppError::internal());
    };
    let started = match sp.start_sso(&idp, StartSso::redirect().relay_state(relay_param)) {
        Ok(value) => value,
        Err(error) => {
            tracing::error!(?error, "could not start SAML sign-in");
            return Ok(connection_error(
                &slug,
                "This SAML connection could not start sign-in.",
                StatusCode::BAD_GATEWAY,
            ));
        }
    };
    let request_id = started.pending.request_id().as_str();
    let target = started
        .outbound
        .redirect_url()
        .map_err(internal)?
        .to_owned();
    state
        .store
        .create_verification_token(&VerificationToken {
            id: format!("vrf_{}:{request_id}", tokens::random_token(20)),
            instance_id: instance.id,
            user_id: Some(connection.id),
            purpose: STATE_PURPOSE.into(),
            authentication_method: None,
            authentication_next: users::validated_authentication_next(
                &state.options.public_origin,
                &slug,
                query.get("next").map(String::as_str),
            ),
            user_auth_epoch: None,
            token_key: tokens::sha256_hex(&relay),
            expires_at: now().saturating_add(STATE_TTL_SECONDS),
            consumed: false,
            failed_attempts: 0,
            created_at: now(),
            email_snapshot: None,
        })
        .await
        .map_err(internal)?;
    let mut response = Redirect::to(&target).into_response();
    set_correlation_cookie(&mut response, &slug, &connection_id, &relay);
    Ok(response)
}

#[utoipa::path(
    post,
    path = "/{slug}/connections/{connection_id}/saml/acs",
    params(("slug" = String, Path), ("connection_id" = String, Path)),
    request_body(content = SamlAcsForm, content_type = "application/x-www-form-urlencoded"),
    responses((status = 303, description = "Successful sign-in redirects to hosted account"), (status = 400, description = "Invalid or expired SAML sign-in")),
    tag = "Connections"
)]
pub async fn saml_acs(
    State(state): State<AppState>,
    Path((slug, connection_id)): Path<(String, String)>,
    headers: HeaderMap,
    Form(form): Form<HashMap<String, String>>,
) -> AppResult<Response> {
    let mut response = match complete_acs(&state, &slug, &connection_id, &headers, &form).await {
        Ok(response) => response,
        Err(error) => error.into_response(),
    };
    clear_correlation_cookie(&mut response, &slug, &connection_id);
    Ok(response)
}

async fn complete_acs(
    state: &AppState,
    slug: &str,
    connection_id: &str,
    headers: &HeaderMap,
    form: &HashMap<String, String>,
) -> AppResult<Response> {
    let Some(instance) = state
        .store
        .find_instance_by_slug(slug)
        .await
        .map_err(internal)?
    else {
        return Ok(not_found_page());
    };
    let Some(connection) =
        find_connection(&state.store, &instance.id, connection_id, "saml").await?
    else {
        return Ok(not_found_page());
    };
    let Some(relay) = form
        .get("RelayState")
        .filter(|value| !value.is_empty() && value.len() <= 80)
    else {
        return Ok(invalid_attempt(slug));
    };
    if !valid_correlation_cookie(headers, relay) {
        return Ok(invalid_attempt(slug));
    }
    let Some(encoded) = form
        .get("SAMLResponse")
        .filter(|value| !value.is_empty() && value.len() <= MAX_SAML_RESPONSE_BASE64)
    else {
        return Ok(invalid_attempt(slug));
    };
    let Ok(xml) = STANDARD.decode(encoded) else {
        return Ok(invalid_attempt(slug));
    };
    if xml.len() > MAX_SAML_RESPONSE_XML || !safe_saml_xml_profile(&xml) {
        return Ok(invalid_attempt(slug));
    }
    let Ok(idp) = idp_descriptor(&connection) else {
        return Ok(invalid_attempt(slug));
    };
    let Ok(sp) = service_provider(state, &connection, slug) else {
        return Ok(invalid_attempt(slug));
    };
    let state_key = tokens::sha256_hex(relay);
    let Some(pending_record) = state
        .store
        .find_verification_token(&state_key)
        .await
        .map_err(internal)?
    else {
        return Ok(invalid_attempt(slug));
    };
    if !eligible_state(&pending_record, &instance.id, &connection.id) {
        return Ok(invalid_attempt(slug));
    }
    let Some(request_id) = pending_record
        .id
        .split_once(':')
        .map(|(_, id)| id)
        .filter(|id| !id.is_empty())
    else {
        return Ok(invalid_attempt(slug));
    };
    let Ok(relay_param) = RelayStateParam::try_from_option(Some(relay.clone())) else {
        return Ok(invalid_attempt(slug));
    };
    let snapshot = PendingSnapshot::<AuthnRequest>::authn_request(
        request_id,
        relay_param,
        idp.entity_id().as_str(),
        "post",
        acs_url(state, slug, &connection.id),
        "post",
    );
    let Ok(pending) = PendingAuthnRequest::from_snapshot(snapshot) else {
        return Ok(invalid_attempt(slug));
    };
    let input = BrowserInput::<SsoResponse>::post(vec![
        FormField::new("SAMLResponse", encoded),
        FormField::new("RelayState", relay),
    ]);
    // The library's replay cache is synchronous; durable create-only claims follow validation.
    let validation =
        SamlValidationContext::new(SystemTime::now(), ReplayPolicy::DisabledForCompatibility);
    let session = match sp.finish_sso(&idp, &pending, input, validation) {
        Ok(value) => value,
        Err(error) => {
            tracing::warn!(?error, "SAML response validation failed");
            return Ok(invalid_attempt(slug));
        }
    };
    let Some(email) = email_from_session(&session) else {
        return Ok(invalid_attempt(slug));
    };
    let name = name_from_session(&session);
    // CAS makes a concurrent callback consume this login attempt at most once.
    if state
        .store
        .consume_verification_token(&state_key, |record| {
            record.id == pending_record.id && eligible_state(record, &instance.id, &connection.id)
        })
        .await
        .map_err(internal)?
        .is_none()
    {
        return Ok(invalid_attempt(slug));
    }
    for key in session.replay_keys() {
        let scoped = format!("{}:{}:{}", instance.id, connection.id, key.cache_key());
        match state
            .store
            .claim_unique("saml-assertion", &scoped, &pending_record.id)
            .await
        {
            Ok(()) => {}
            Err(error) if error.is_conflict() => return Ok(invalid_attempt(slug)),
            Err(error) => return Err(internal(error)),
        }
    }
    finish_federated_sign_in(
        state,
        &instance,
        slug,
        headers,
        &email,
        name.as_deref(),
        &format!("saml:{}", connection.id),
        pending_record.authentication_next.as_deref(),
    )
    .await
}

fn eligible_state(record: &VerificationToken, instance_id: &str, connection_id: &str) -> bool {
    record.instance_id == instance_id
        && record.user_id.as_deref() == Some(connection_id)
        && record.purpose == STATE_PURPOSE
        && !record.consumed
        && record.expires_at > now()
}

fn invalid_attempt(slug: &str) -> Response {
    connection_error(
        slug,
        "This SAML sign-in attempt is invalid, expired, or already used.",
        StatusCode::BAD_REQUEST,
    )
}

fn set_correlation_cookie(response: &mut Response, slug: &str, connection_id: &str, relay: &str) {
    let value = format!(
        "{STATE_COOKIE}={}; Path={}; Max-Age={STATE_TTL_SECONDS}; Secure; HttpOnly; SameSite=None",
        tokens::sha256_hex(relay),
        acs_path(slug, connection_id)
    );
    if let Ok(value) = HeaderValue::from_str(&value) {
        response.headers_mut().append(header::SET_COOKIE, value);
    }
}

fn clear_correlation_cookie(response: &mut Response, slug: &str, connection_id: &str) {
    let value = format!(
        "{STATE_COOKIE}=; Path={}; Max-Age=0; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Secure; HttpOnly; SameSite=None",
        acs_path(slug, connection_id)
    );
    if let Ok(value) = HeaderValue::from_str(&value) {
        response.headers_mut().append(header::SET_COOKIE, value);
    }
}

fn valid_correlation_cookie(headers: &HeaderMap, relay: &str) -> bool {
    let Some(cookie) = cookie_value(headers, STATE_COOKIE) else {
        return false;
    };
    let expected = tokens::sha256_hex(relay);
    cookie.as_bytes().ct_eq(expected.as_bytes()).into()
}

const SAML_PROTOCOL_NS: &[u8] = b"urn:oasis:names:tc:SAML:2.0:protocol";
const SAML_ASSERTION_NS: &[u8] = b"urn:oasis:names:tc:SAML:2.0:assertion";
const XMLDSIG_NS: &[u8] = b"http://www.w3.org/2000/09/xmldsig#";

#[derive(Clone, Copy, PartialEq, Eq)]
enum ElementRole {
    Response,
    Assertion,
    Signature,
    Other,
}

/// Reject unsupported multi-signature and wrapping profiles before the library verifier sees them.
/// `saml-rs` 0.5.0 may otherwise accept the first valid signature despite a later invalid one.
fn safe_saml_xml_profile(xml: &[u8]) -> bool {
    if xml.len() > MAX_SAML_RESPONSE_XML {
        return false;
    }
    let Ok(xml) = std::str::from_utf8(xml) else {
        return false;
    };
    let limits = saml_rs::xml::XmlLimits {
        max_bytes: MAX_SAML_RESPONSE_XML,
        max_depth: 64,
        max_nodes: 10_000,
        max_attributes_per_element: 64,
        max_attribute_value_bytes: 16 * 1024,
        max_text_bytes: MAX_SAML_RESPONSE_XML,
    };
    if saml_rs::xml::dom::parse_with_limits(xml, limits).is_err() {
        return false;
    }
    let mut reader = NsReader::from_str(xml);
    reader.config_mut().check_end_names = true;
    let mut stack = Vec::new();
    let mut root_seen = false;
    let mut assertion_count = 0;
    let mut signature_count = 0;
    let mut node_count = 0;
    loop {
        let Ok((namespace, event)) = reader.read_resolved_event() else {
            return false;
        };
        match event {
            Event::Start(ref start) | Event::Empty(ref start) => {
                node_count += 1;
                if node_count > 10_000 || stack.len() > 64 {
                    return false;
                }
                let mut attributes = 0;
                for attribute in start.attributes() {
                    if attribute.is_err() {
                        return false;
                    }
                    attributes += 1;
                    if attributes > 64 {
                        return false;
                    }
                }
                let local = start.local_name();
                let parent = stack.last().copied();
                let ns = match namespace {
                    ResolveResult::Bound(value) => Some(value.0),
                    ResolveResult::Unbound => None,
                    ResolveResult::Unknown(_) => return false,
                };
                let role = match local.as_ref() {
                    b"Response"
                        if ns == Some(SAML_PROTOCOL_NS) && parent.is_none() && !root_seen =>
                    {
                        root_seen = true;
                        ElementRole::Response
                    }
                    b"Assertion"
                        if ns == Some(SAML_ASSERTION_NS)
                            && parent == Some(ElementRole::Response) =>
                    {
                        assertion_count += 1;
                        ElementRole::Assertion
                    }
                    b"Signature"
                        if ns == Some(XMLDSIG_NS)
                            && matches!(
                                parent,
                                Some(ElementRole::Response | ElementRole::Assertion)
                            ) =>
                    {
                        signature_count += 1;
                        ElementRole::Signature
                    }
                    b"Response" | b"Assertion" | b"Signature" => return false,
                    _ if parent.is_some() => ElementRole::Other,
                    _ => return false,
                };
                if assertion_count > 1 || signature_count > 1 {
                    return false;
                }
                if matches!(event, Event::Start(_)) {
                    stack.push(role);
                }
            }
            Event::End(_) => {
                if stack.pop().is_none() {
                    return false;
                }
            }
            Event::Text(text) => {
                if stack.is_empty() && !text.as_ref().iter().all(u8::is_ascii_whitespace) {
                    return false;
                }
            }
            Event::Decl(_) if !root_seen => {}
            Event::Eof => break,
            // Entity references, DTDs, comments, CDATA and processing instructions
            // are outside the accepted SAML browser profile.
            _ => return false,
        }
    }
    root_seen && stack.is_empty() && assertion_count == 1 && signature_count == 1
}

fn email_from_session(session: &saml_rs::SsoSession) -> Option<String> {
    const EMAIL_ATTRIBUTES: &[&str] = &[
        "email",
        "mail",
        "emailAddress",
        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
        "urn:oid:0.9.2342.19200300.100.1.3",
    ];
    let from_attribute = EMAIL_ATTRIBUTES.iter().find_map(|name| {
        let values = session.attributes().get(name)?.values();
        (values.len() == 1).then(|| values[0].as_str())
    });
    let candidate = from_attribute
        .unwrap_or_else(|| session.name_id().value())
        .trim();
    valid_email(candidate).then(|| candidate.to_owned())
}

fn valid_email(candidate: &str) -> bool {
    candidate.len() <= 320
        && !candidate.contains(char::is_whitespace)
        && candidate.split_once('@').is_some_and(|(local, domain)| {
            !local.is_empty()
                && domain.contains('.')
                && !domain.starts_with('.')
                && !domain.ends_with('.')
        })
}

fn name_from_session(session: &saml_rs::SsoSession) -> Option<String> {
    [
        "displayName",
        "name",
        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name",
    ]
    .iter()
    .find_map(|key| session.attributes().get(key)?.values().first())
    .map(|value| value.as_str().trim())
    .filter(|value| !value.is_empty() && value.len() <= 200)
    .map(str::to_owned)
}

#[cfg(test)]
mod tests {
    use super::*;
    use saml_rs::{
        CertificatePem, Credentials, IdpConfig, IdpValidationPolicy, NameId, PrivateKeyPem,
        RespondSso, SpDescriptor, SsoEndpoint, Subject,
    };

    // Public saml-rs test fixture key pair, embedded so tests run offline.
    const TEST_KEY: &str = r#"-----BEGIN PRIVATE KEY-----
MIIEwgIBADANBgkqhkiG9w0BAQEFAASCBKwwggSoAgEAAoIBAgC6Jl6iPARyB13z
JEJ12SEuGgpJwhMmXVVxStswVrvrKD/0oWM6CJjbtjhzMvvVcrLcvnYZBdyvJeE5
eu3613wCuwNX4BNWYu38TDVVOYbmMpuwlB7e1iC6W92XqswTvK0LW+TjQsYC0HTx
8/gdtVQs5LocSwVa3C0rEjbChWB8JTfuck4LP1JVawzFHCOY2fWXI6ct3MdnzvJT
ojIgZELPAuE3T2NWhgrfpco+X5cXaZN44MBcnlsYfMF9LU8uGL3Mbtzy3TAsxVtL
voLPpOkr+hMmdMEybPBnqwuWtsrdfLuB5FXuuFZ8SZj3tZeQYtRreMp+rktMB/re
vPlmPOB5nwIDAQABAoIBAgCwsQD8n1lc3y9HPjCzafE7sE35qvTAYrFag0JAxONE
mAT08Eeea1CkpHc6qbcu6Ntr+oFgyRarTZpWFCBWDDnS4a6Pt8rDIc5hv/iTt7Ib
SQhM+JvAyqFwIwjYEK/7QAlFEenV6ajIPRP0Ia5ujJKktksN1gv0La/WBUjjJPTr
gdKDgP4SgEHwhDF3rgzk/284Dbmr4pOxvi290AnHrVER+oPlxy/WS4mOEBYVU2Rq
TbP07ej+ifYO/w9ZjJAc5l9UK2NDsv3JCUgui0PBgZ27z3dZ2TD2VbZXqSS4jahG
UUhwCT0sOzIWVsZypv4PvTHJl/uvgS1Be01n/FAtcJGAKQKBgQ49glSkdtfJtjQm
blqbacabkox+EM8EGkc9iveveD81E0gUYTL7AjVawowVYKQnYSZfsCdU7feLPQH2
bwjzAgbiY+QbbFcTT9FJu5D31qM4jqk24Bu2vjecHpEy8sJKkvoqv0CdWTUKhk98
7enh63LWo4Hjs6FVMAh8QKwsHjeMfQKBgQ0Sc4eO4ZSFuwCx8L4gG7MlvIeqVbHC
8kwDzJY3qECxDaDD1ogbGuDVa9p8bnZNmhHE6+nyep7AGvXKzWUzWqYYz2nqxwuC
/MAQHN6ruKFDaWzrDCfBMfwLsif4SgpcKkJj+1Y+LjPe1tgBw45cco9cWxZLPvVL
yxEAVTbFWXFlSwKBgQu7F/ZqVYyGGpbzYc06YfS+jAc4gthG5O7y/9vyrPhE3NFw
GHJK3RLe5Y1Ivwf7eMiH4zFDgZV/Go7XV7jjlzPco7Vx8dn5irM6Lk3KHQLwwHUd
Q5kQ/boJ3hR3CAyOKm3zcQHlnWtYdDRfEg6tkaxUrPV/gqbQ6nTTBuPOpEXWcQKB
gQnqSvMxr21mmleGoOK1nA0gvIXy75ksE3kREKeIg/i902Zz5U/Lr3GGsI5C/86A
QjLkOUV0hQnRESIKuAzhDQsbmofuaxgSPQC5uAw2GI9JgLf6+XdWFUHm5TVoIVEG
Y4+EIuphs83oYvHpNJnRCZwwI28fmBubZ+X3aKtoudVHTQKBgQCKN34ZGQUgs3OY
0VNlrlM5BMqO4uJD/kv5BgepqfbVEud2Mwmu+W1nPKv0rdWyIZunL2xVGD/9yRk0
ruC2q4dCQvFV2TsxN7Pkf+bvm9Ep9XokQE79g7EVty3mf8vYVv5KXDXEH808YxqU
gFQYnB76DUsI08o13SQtjDCm/oLxSA==
-----END PRIVATE KEY-----"#;
    const TEST_CERT: &str = r#"-----BEGIN CERTIFICATE-----
MIID0TCCArigAwIBAgIBADANBgkqhkiG9w0BAQsFADCBgTELMAkGA1UEBhMCaGsx
EjAQBgNVBAgMCUhvbmcgS29uZzETMBEGA1UECgwKbm9kZS1zYW1sMjEXMBUGA1UE
AwwOc2FtbGlmeS5qcy5vcmcxCzAJBgNVBAcMAkhLMSMwIQYJKoZIhvcNAQkBFhRu
b2RlLnNhbWwyQGdtYWlsLmNvbTAeFw0yMzAxMDUyMjQ3NTBaFw0zMzAxMDIyMjQ3
NTBaMIGBMQswCQYDVQQGEwJoazESMBAGA1UECAwJSG9uZyBLb25nMRMwEQYDVQQK
DApub2RlLXNhbWwyMRcwFQYDVQQDDA5zYW1saWZ5LmpzLm9yZzELMAkGA1UEBwwC
SEsxIzAhBgkqhkiG9w0BCQEWFG5vZGUuc2FtbDJAZ21haWwuY29tMIIBIzANBgkq
hkiG9w0BAQEFAAOCARAAMIIBCwKCAQIAuiZeojwEcgdd8yRCddkhLhoKScITJl1V
cUrbMFa76yg/9KFjOgiY27Y4czL71XKy3L52GQXcryXhOXrt+td8ArsDV+ATVmLt
/Ew1VTmG5jKbsJQe3tYgulvdl6rME7ytC1vk40LGAtB08fP4HbVULOS6HEsFWtwt
KxI2woVgfCU37nJOCz9SVWsMxRwjmNn1lyOnLdzHZ87yU6IyIGRCzwLhN09jVoYK
36XKPl+XF2mTeODAXJ5bGHzBfS1PLhi9zG7c8t0wLMVbS76Cz6TpK/oTJnTBMmzw
Z6sLlrbK3Xy7geRV7rhWfEmY97WXkGLUa3jKfq5LTAf63rz5ZjzgeZ8CAwEAAaNQ
ME4wHQYDVR0OBBYEFIW8z2CmYsUvTbX8IFHE+IdbkcsHMB8GA1UdIwQYMBaAFIW8
z2CmYsUvTbX8IFHE+IdbkcsHMAwGA1UdEwQFMAMBAf8wDQYJKoZIhvcNAQELBQAD
ggECAAvSnm6IQYmPPVpoUvwC3K3jh28HQ/8S5QMgRGw+RcqgJbo5wIAn0OOUJLfR
rLtcx2UfaFPn3QKvGOrX71ekb6FF0hzEq+U3Mbv2KEWBEwbu5D+N93uvf3ryeM7o
X4t6Qi5gKe486PrOBu2CyUtveFBqECWCGRG5yN+/4bd2zh/hug3lD28hicA7rsnA
FOrEmnhnDhzWzoG+ih1uCzjui21gFGeKRspWkXf+4Vgc7lL+tYCI0sVfe1huRwFh
SkTC5yT/7mYw4aLlRAKOxAEMHQjj5bFyr8NZa296YIcdBbdRm8oqGDqWcK4wcu/I
yFtbTMIWwNL9n/lLh695eNwDmg28
-----END CERTIFICATE-----"#;
    const FOREIGN_CERT: &str = r#"-----BEGIN CERTIFICATE-----
MIIDlzCCAn+gAwIBAgIJAO1ymQc33+bWMA0GCSqGSIb3DQEBCwUAMGIxCzAJBgNV
BAYTAkhLMRMwEQYDVQQIDApTb21lLVN0YXRlMRowGAYDVQQKDBFJZGVudGl0eSBQ
cm92aWRlcjEUMBIGA1UECwwLRGV2ZWxvcG1lbnQxDDAKBgNVBAMMA0lEUDAeFw0x
NTA3MDUxODAyMjdaFw0xODA3MDQxODAyMjdaMGIxCzAJBgNVBAYTAkhLMRMwEQYD
VQQIDApTb21lLVN0YXRlMRowGAYDVQQKDBFJZGVudGl0eSBQcm92aWRlcjEUMBIG
A1UECwwLRGV2ZWxvcG1lbnQxDDAKBgNVBAMMA0lEUDCCASIwDQYJKoZIhvcNAQEB
BQADggEPADCCAQoCggEBAODZsWhCe+yG0PalQPTUoD7yko5MTWMCRxJ8hSm2k7mG
3Eg/Y2v0EBdCmTw7iDCevRqUmbmFnq7MROyV4eriJzh0KabAdZf7/k6koghst3ZU
tWOwzshyxkBtWDwGmBpQGTGsKxJ8M1js3aSqNRXBT4OBWM9w2Glt1+8ty30RhYv3
pSF+/HHLH7Ac+vLSIAlokaFW34RWTcJ/8rADuRWlXih4GfnIu0W/ncm5nTSaJiRA
vr3dGDRO/khiXoJdbbOj7dHPULxVGbH9IbPK76TCwLbF7ikIMsPovVbTrpyL6vsb
VUKeEl/5GKppTwp9DLAOeoSYpCYkkDkYKu9TRQjF02MCAwEAAaNQME4wHQYDVR0O
BBYEFP2ut2AQdy6D1dwdwK740IHmbh38MB8GA1UdIwQYMBaAFP2ut2AQdy6D1dwd
wK740IHmbh38MAwGA1UdEwQFMAMBAf8wDQYJKoZIhvcNAQELBQADggEBANMZUoPN
mHzgja2PYkbvBYMHmpvUkVoiuvQ9cJPlqGTB2CRfG68BNNs/Clz8P7cIrAdkhCUw
i1rSBhDuslGFNrSaIpv6B10FpBuKwef3G7YrPWFNEN6khY7aHNWSTHqKgs1DrGef
2B9hvkrnHWbQVSVXrBFKe1wTCqcgGcOpYoSK7L8C6iX6uIA/uZYnVQ4NgBrizJ0a
zkjdegz3hwO/gt4malEURy8D85/AAVt6PAzhpb9VJUGxSXr/EfntVUEz3L2gUFWW
k1CnZFyz0rIOEt/zPmeAY8BLyd/Tjxm4Y+gwNazKq5y9AJS+m858b/nM4QdCnUE4
yyoWAJDUHiAmvFA=
-----END CERTIFICATE-----"#;

    fn profile_xml(body: &str) -> String {
        format!(
            "<p:Response xmlns:p=\"{}\" xmlns:s=\"{}\" xmlns:ds=\"{}\">{body}</p:Response>",
            std::str::from_utf8(SAML_PROTOCOL_NS).unwrap(),
            std::str::from_utf8(SAML_ASSERTION_NS).unwrap(),
            std::str::from_utf8(XMLDSIG_NS).unwrap(),
        )
    }

    #[test]
    fn rejects_multiple_signatures_wrapping_and_namespace_spoofing() {
        assert!(safe_saml_xml_profile(
            profile_xml("<s:Assertion><ds:Signature/></s:Assertion>").as_bytes()
        ));
        assert!(safe_saml_xml_profile(
            profile_xml("<ds:Signature/><s:Assertion/>").as_bytes()
        ));
        assert!(!safe_saml_xml_profile(
            profile_xml("<ds:Signature/><s:Assertion><ds:Signature/></s:Assertion>").as_bytes()
        ));
        assert!(!safe_saml_xml_profile(
            profile_xml("<s:Assertion/><s:Assertion/><ds:Signature/>").as_bytes()
        ));
        assert!(!safe_saml_xml_profile(
            profile_xml(
                "<s:Assertion><x:Other xmlns:x=\"urn:evil\"><ds:Signature/></x:Other></s:Assertion>"
            )
            .as_bytes()
        ));
        assert!(!safe_saml_xml_profile(
            profile_xml("<s:Assertion/><ds:Signature/>")
                .replace(XMLDSIG_NS_STR, "urn:evil")
                .as_bytes()
        ));
        assert!(!safe_saml_xml_profile(
            profile_xml("<s:Assertion/><ds:Signature/>")
                .replace(SAML_ASSERTION_NS_STR, "urn:evil")
                .as_bytes()
        ));
        assert!(!safe_saml_xml_profile(
            format!(
                "{}{}",
                profile_xml("<s:Assertion/><ds:Signature/>"),
                profile_xml("")
            )
            .as_bytes()
        ));
        assert!(!safe_saml_xml_profile(
            format!(
                "<!DOCTYPE foo>{}",
                profile_xml("<s:Assertion/><ds:Signature/>")
            )
            .as_bytes()
        ));
    }

    const XMLDSIG_NS_STR: &str = "http://www.w3.org/2000/09/xmldsig#";
    const SAML_ASSERTION_NS_STR: &str = "urn:oasis:names:tc:SAML:2.0:assertion";

    type SignedExchange = (
        Saml<saml_rs::Sp>,
        IdpDescriptor,
        PendingAuthnRequest,
        Vec<FormField>,
        String,
    );

    fn signed_exchange(
        assertion_signed: bool,
    ) -> Result<SignedExchange, Box<dyn std::error::Error>> {
        const SP_ID: &str = "https://sp.example.test/acs";
        const IDP_ID: &str = "https://idp.example.test/metadata";
        const IDP_SSO: &str = "https://idp.example.test/sso";
        let sp = Saml::sp(
            SpConfig::builder(EntityId::try_new(SP_ID)?)
                .acs_endpoint(AcsEndpoint::post(SP_ID)?)
                .validation(sp_validation_policy())
                .build()?,
        )?;
        let mut metadata_policy = sp_validation_policy();
        if assertion_signed {
            metadata_policy.assertions = AssertionSignaturePolicy::RequireSigned;
        }
        let metadata_sp = Saml::sp(
            SpConfig::builder(EntityId::try_new(SP_ID)?)
                .acs_endpoint(AcsEndpoint::post(SP_ID)?)
                .validation(metadata_policy)
                .build()?,
        )?;
        let sp_descriptor = SpDescriptor::from_metadata_xml_for(
            EntityId::try_new(SP_ID)?,
            metadata_sp.metadata_xml(),
            MetadataTrustPolicy::UnsignedForCompatibility,
        )?;
        let idp = Saml::idp(
            IdpConfig::builder(EntityId::try_new(IDP_ID)?)
                .sso_endpoint(SsoEndpoint::redirect(IDP_SSO)?)
                .credentials(Credentials {
                    signing_key: Some(PrivateKeyPem::new(TEST_KEY)),
                    signing_certificate: Some(CertificatePem::new(TEST_CERT)),
                    ..Credentials::default()
                })
                .validation(IdpValidationPolicy::compatibility())
                .build()?,
        )?;
        let idp_descriptor = IdpDescriptor::from_metadata_xml_for(
            EntityId::try_new(IDP_ID)?,
            idp.metadata_xml(),
            MetadataTrustPolicy::UnsignedForCompatibility,
        )?;
        let started = sp.start_sso(
            &idp_descriptor,
            StartSso::redirect().relay_state(RelayStateParam::try_from_option(Some("test-relay"))?),
        )?;
        let redirect = reqwest::Url::parse(started.outbound.redirect_url()?)?;
        let received = idp.receive_sso(
            &sp_descriptor,
            BrowserInput::<AuthnRequest>::redirect(redirect.query().ok_or("missing SAML query")?),
            SamlValidationContext::new(SystemTime::now(), ReplayPolicy::DisabledForCompatibility),
        )?;
        let response = idp.respond_sso(
            &sp_descriptor,
            &received,
            Subject::new(NameId::new("alice@example.test", None), Vec::new()),
            RespondSso::post(),
        )?;
        let fields = response.post_form()?.fields().to_vec();
        let xml = String::from_utf8(
            STANDARD.decode(
                response
                    .post_form()?
                    .value("SAMLResponse")
                    .ok_or("missing SAML response")?,
            )?,
        )?;
        Ok((sp, idp_descriptor, started.pending, fields, xml))
    }

    #[test]
    fn validates_signed_response_and_signed_assertion_and_rejects_tampering()
    -> Result<(), Box<dyn std::error::Error>> {
        for assertion_signed in [false, true] {
            let (sp, idp, pending, fields, xml) = signed_exchange(assertion_signed)?;
            assert!(safe_saml_xml_profile(xml.as_bytes()));
            let session = sp.finish_sso(
                &idp,
                &pending,
                BrowserInput::<SsoResponse>::post(fields.clone()),
                SamlValidationContext::new(
                    SystemTime::now(),
                    ReplayPolicy::DisabledForCompatibility,
                ),
            )?;
            assert_eq!(session.name_id().value(), "alice@example.test");
            let tampered = xml.replace("alice@example.test", "mallory@example.test");
            assert_ne!(tampered, xml);
            let tampered_fields = fields
                .into_iter()
                .map(|field| {
                    if field.name() == "SAMLResponse" {
                        FormField::new("SAMLResponse", STANDARD.encode(tampered.as_bytes()))
                    } else {
                        field
                    }
                })
                .collect();
            assert!(
                sp.finish_sso(
                    &idp,
                    &pending,
                    BrowserInput::<SsoResponse>::post(tampered_fields),
                    SamlValidationContext::new(
                        SystemTime::now(),
                        ReplayPolicy::DisabledForCompatibility
                    )
                )
                .is_err()
            );
            let root_end = xml.rfind("</").ok_or("missing response end tag")?;
            let mixed = format!(
                "{}<ds:Signature xmlns:ds=\"{XMLDSIG_NS_STR}\"/>{}",
                &xml[..root_end],
                &xml[root_end..]
            );
            assert!(!safe_saml_xml_profile(mixed.as_bytes()));
            let assertion_start = xml.find("<saml:Assertion").ok_or("missing assertion")?;
            let assertion_end = xml
                .find("</saml:Assertion>")
                .ok_or("missing assertion end")?
                + "</saml:Assertion>".len();
            let wrapped = format!(
                "{}{}{}",
                &xml[..root_end],
                &xml[assertion_start..assertion_end],
                &xml[root_end..]
            );
            assert!(!safe_saml_xml_profile(wrapped.as_bytes()));
            let response_id = xml
                .split_once(" ID=\"")
                .ok_or("missing response ID")?
                .1
                .split_once('"')
                .ok_or("missing response ID end")?
                .0;
            let duplicate_id = xml.replacen(
                "<saml:Issuer>",
                &format!("<saml:Issuer ID=\"{response_id}\">"),
                1,
            );
            assert_ne!(duplicate_id, xml);
            assert!(safe_saml_xml_profile(duplicate_id.as_bytes()));
            let duplicate_fields = vec![
                FormField::new("SAMLResponse", STANDARD.encode(duplicate_id.as_bytes())),
                FormField::new("RelayState", "test-relay"),
            ];
            assert!(
                sp.finish_sso(
                    &idp,
                    &pending,
                    BrowserInput::<SsoResponse>::post(duplicate_fields),
                    SamlValidationContext::new(
                        SystemTime::now(),
                        ReplayPolicy::DisabledForCompatibility
                    )
                )
                .is_err()
            );
        }
        Ok(())
    }

    #[test]
    fn rejects_a_valid_signature_from_an_unpinned_certificate()
    -> Result<(), Box<dyn std::error::Error>> {
        let (sp, _, pending, fields, _) = signed_exchange(false)?;
        let foreign_metadata = try_generate_idp_metadata(&IdpMetadataConfig {
            entity_id: "https://idp.example.test/metadata".into(),
            signing_certs: vec![FOREIGN_CERT.into()],
            single_sign_on_service: vec![Endpoint::new(
                Binding::Redirect,
                "https://idp.example.test/sso",
            )],
            ..IdpMetadataConfig::default()
        })?;
        let foreign = IdpDescriptor::from_metadata_xml_for(
            EntityId::try_new("https://idp.example.test/metadata")?,
            &foreign_metadata,
            MetadataTrustPolicy::UnsignedForCompatibility,
        )?;
        assert!(
            sp.finish_sso(
                &foreign,
                &pending,
                BrowserInput::<SsoResponse>::post(fields),
                SamlValidationContext::new(
                    SystemTime::now(),
                    ReplayPolicy::DisabledForCompatibility
                )
            )
            .is_err()
        );
        Ok(())
    }

    #[test]
    fn correlation_cookie_is_required_and_bound_to_relay() {
        let mut headers = HeaderMap::new();
        assert!(!valid_correlation_cookie(&headers, "one"));
        headers.insert(
            header::COOKIE,
            HeaderValue::from_str(&format!("{STATE_COOKIE}={}", tokens::sha256_hex("one")))
                .unwrap(),
        );
        assert!(valid_correlation_cookie(&headers, "one"));
        assert!(!valid_correlation_cookie(&headers, "two"));
    }

    #[test]
    fn pending_state_is_scoped_unconsumed_and_unexpired() {
        let mut record = VerificationToken {
            id: "vrf_request".into(),
            instance_id: "ins_1".into(),
            user_id: Some("con_1".into()),
            purpose: STATE_PURPOSE.into(),
            authentication_method: None,
            authentication_next: None,
            user_auth_epoch: None,
            token_key: "key".into(),
            expires_at: now() + 60,
            consumed: false,
            failed_attempts: 0,
            created_at: now(),
            email_snapshot: None,
        };
        assert!(eligible_state(&record, "ins_1", "con_1"));
        assert!(!eligible_state(&record, "ins_2", "con_1"));
        assert!(!eligible_state(&record, "ins_1", "con_2"));
        record.consumed = true;
        assert!(!eligible_state(&record, "ins_1", "con_1"));
        record.consumed = false;
        record.expires_at = now() - 1;
        assert!(!eligible_state(&record, "ins_1", "con_1"));
        record.expires_at = now() + 60;
        record.purpose = "other".into();
        assert!(!eligible_state(&record, "ins_1", "con_1"));
    }
}
