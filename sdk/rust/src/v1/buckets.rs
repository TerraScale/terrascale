//! Project-scoped file bucket metadata and shared end-user policies.
use reqwest::{Method, StatusCode};
use serde::{Deserialize, Deserializer};

use super::{client::Req, ids::valid_entity_id, rules::CollectionRules, Client, Error, Result};

/// File bucket metadata. Management requires project credentials.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct FileBucket {
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub id: String,
    pub name: String,
    pub region: String,
    pub rules: CollectionRules,
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
        rules: Option<&CollectionRules>,
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
        rules: Option<&CollectionRules>,
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
