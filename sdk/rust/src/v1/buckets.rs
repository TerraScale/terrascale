//! Project-scoped file bucket metadata and end-user policies.
use reqwest::{Method, StatusCode};
use serde::{Deserialize, Deserializer, Serialize};

use super::{client::Req, ids::valid_entity_id, Client, Error, Result};

/// Independent grants for one operation. All-false grants deny access.
/// `public` means authenticated end-users, never anonymous requests.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct ReadWriteGrants {
    pub owner: bool,
    pub public: bool,
    /// Trusted, case-sensitive roles; an empty array grants no role access.
    #[serde(deserialize_with = "null_deny")]
    pub roles: Vec<String>,
}

/// Declarative end-user bucket policy; project credentials manage this policy.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct BucketRules {
    /// JSON Pointer to the immutable owner subject, or no ownership grant.
    /// Serialized explicitly as null when absent.
    pub owner: Option<String>,
    #[serde(deserialize_with = "null_deny")]
    pub read: ReadWriteGrants,
    #[serde(deserialize_with = "null_deny")]
    pub write: ReadWriteGrants,
}

// Null responses carry no grants. Missing fields remain malformed, while
// serialization always emits complete operation objects and role arrays.
fn null_deny<'de, D, T>(d: D) -> std::result::Result<T, D::Error>
where
    D: serde::Deserializer<'de>,
    T: Deserialize<'de> + Default,
{
    Ok(Option::<T>::deserialize(d)?.unwrap_or_default())
}

/// File bucket metadata. Management requires project credentials.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct FileBucket {
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub id: String,
    pub name: String,
    pub region: String,
    pub rules: BucketRules,
}

/// At most 50 buckets in ID order. `next` is an exclusive ULID continuation.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct FileBucketPage {
    pub items: Vec<FileBucket>,
    #[serde(deserialize_with = "bucket_cursor")]
    pub next: Option<String>,
}

fn bucket_cursor<'de, D: Deserializer<'de>>(d: D) -> std::result::Result<Option<String>, D::Error> {
    let next = Option::<String>::deserialize(d)?;
    if next.as_deref().is_some_and(|value| !valid_entity_id(value)) {
        return Err(serde::de::Error::custom(
            "bucket cursor must be a canonical uppercase ULID or null",
        ));
    }
    Ok(next)
}

fn check_id(id: &str) -> Result<()> {
    if valid_entity_id(id) {
        Ok(())
    } else {
        Err(Error::InvalidInput(
            "bucket ID or cursor must be a canonical uppercase ULID".into(),
        ))
    }
}

impl Client {
    /// Lists one page of buckets; pass `page.next` as `after` for the next page.
    /// Database-restricted and end-user credentials cannot manage buckets.
    pub async fn list_buckets(&self, after: Option<&str>) -> Result<FileBucketPage> {
        let mut query = Vec::new();
        if let Some(after) = after {
            check_id(after)?;
            query.push(("after", after.to_owned()));
        }
        self.management(
            Method::GET,
            vec!["v1", "buckets"],
            &query,
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }

    /// Creates a bucket. Null rules deny end users; grants use immutable file metadata.
    pub async fn create_bucket(
        &self,
        name: &str,
        rules: Option<&BucketRules>,
    ) -> Result<FileBucket> {
        self.management(
            Method::POST,
            vec!["v1", "buckets"],
            &[],
            Some(&serde_json::json!({"name": name, "rules": rules})),
            StatusCode::CREATED,
        )
        .await
    }

    /// Replaces the entire bucket policy using the `{rules: ...}` wrapper.
    /// Null rules deny end-user access; existing signed capabilities expire normally.
    pub async fn set_bucket_rules(
        &self,
        bucket: &str,
        rules: Option<&BucketRules>,
    ) -> Result<FileBucket> {
        check_id(bucket)?;
        self.management(
            Method::PUT,
            vec!["v1", "buckets", bucket, "rules"],
            &[],
            Some(&serde_json::json!({"rules": rules})),
            StatusCode::OK,
        )
        .await
    }

    /// Deletes a bucket. Physical cleanup is queued; reserved bytes can remain billed.
    pub async fn delete_bucket(&self, bucket: &str) -> Result<()> {
        check_id(bucket)?;
        self.send_project(Req::new(Method::DELETE, vec!["v1", "buckets", bucket]))
            .await?
            .check_status(&[StatusCode::NO_CONTENT])
    }
}
