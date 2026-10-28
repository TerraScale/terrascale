//! Schema and secondary-index tooling, using the public project's JSON contract.
use super::{
    client::Req,
    documents::{Collection, Database},
    errors::{Error, Result},
    ids::{check_database_selector, check_name},
};
use reqwest::{Method, StatusCode};
use serde::{de::DeserializeOwned, Deserialize, Serialize};

/// Secondary index strategy. Omission in a create request uses a B-tree.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum IndexKind {
    #[default]
    BTree,
    Text,
    Vector,
}

/// Optional typed body for [`Database::create_index`]; raw JSON remains supported.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct IndexRequest {
    pub collection: String,
    /// One to four dotted member paths.
    pub fields: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub kind: Option<IndexKind>,
    /// Required vector length (1 to 4096) when `kind` is `Vector`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub dimensions: Option<u32>,
}

/// Secondary index returned by the create and list routes.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Index {
    pub name: String,
    pub collection: String,
    pub fields: Vec<String>,
    pub state: String,
    #[serde(default)]
    pub kind: IndexKind,
    /// Vector length; absent for B-tree and text indexes.
    #[serde(
        default,
        deserialize_with = "index_dimensions",
        skip_serializing_if = "Option::is_none"
    )]
    pub dimensions: Option<u32>,
}

fn index_dimensions<'de, D: serde::Deserializer<'de>>(
    deserializer: D,
) -> std::result::Result<Option<u32>, D::Error> {
    use serde::de::Error as _;
    let value = Option::<serde_json::Value>::deserialize(deserializer)?;
    let Some(value) = value else { return Ok(None) };
    let dimension = match value {
        serde_json::Value::Number(n) => n.as_u64(),
        serde_json::Value::String(s) => super::types::u64_string::parse_canonical_u64(&s),
        _ => None,
    };
    match dimension {
        Some(n @ 1..=4096) => Ok(Some(n as u32)),
        _ => Err(D::Error::custom(
            "index dimensions must be an integer from 1 to 4096",
        )),
    }
}

impl Database {
    pub(crate) async fn management_delete(&self, suffix: &[&str]) -> Result<()> {
        check_database_selector(self.name())?;
        let mut segments = vec!["v1", "databases", self.name()];
        segments.extend_from_slice(suffix);
        self.client()
            .send_project(Req::new(Method::DELETE, segments))
            .await?
            .check_status(&[StatusCode::NO_CONTENT])
    }

    /// `GET D/schema`; inferred collections, counts and bytes.
    pub async fn get_database_schema<T: DeserializeOwned>(
        &self,
        query: &[(&str, String)],
    ) -> Result<T> {
        self.management(Method::GET, &["schema"], query, None::<&()>, StatusCode::OK)
            .await
    }
    /// `GET D/schema/{collection}`; management schema inference.
    pub async fn get_collection_schema<T: DeserializeOwned>(&self, collection: &str) -> Result<T> {
        check_name("collection", collection)?;
        self.management(
            Method::GET,
            &["schema", collection],
            &[],
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
    /// `PUT D/schema/{collection}`; camelCase uniqueness declaration.
    pub async fn declare_collection_schema<T: DeserializeOwned>(
        &self,
        collection: &str,
        body: &impl Serialize,
    ) -> Result<T> {
        check_name("collection", collection)?;
        self.management(
            Method::PUT,
            &["schema", collection],
            &[],
            Some(body),
            StatusCode::OK,
        )
        .await
    }
    /// `GET D/indexes`; optional `collection` selector.
    pub async fn list_indexes<T: DeserializeOwned>(&self, query: &[(&str, String)]) -> Result<T> {
        self.management(
            Method::GET,
            &["indexes"],
            query,
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
    /// `POST D/indexes`; body contains `collection`, `fields`, optional `name`
    /// and `kind` (`btree` by default, `text` or `vector`). Vector indexes also
    /// require `dimensions` (1 to 4096).
    pub async fn create_index<T: DeserializeOwned>(&self, body: &impl Serialize) -> Result<T> {
        self.management(
            Method::POST,
            &["indexes"],
            &[],
            Some(body),
            StatusCode::CREATED,
        )
        .await
    }
    /// `DELETE D/indexes/{index}`; index selectors are names.
    pub async fn drop_index(&self, index: &str) -> Result<()> {
        check_name("index", index)?;
        self.management_delete(&["indexes", index]).await
    }
    /// `GET D/auto-index/{collection}`.
    pub async fn get_auto_index_policy<T: DeserializeOwned>(&self, collection: &str) -> Result<T> {
        check_name("collection", collection)?;
        self.management(
            Method::GET,
            &["auto-index", collection],
            &[],
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
    /// `PUT D/auto-index/{collection}`; body contains `enabled`.
    pub async fn set_auto_index_policy<T: DeserializeOwned>(
        &self,
        collection: &str,
        body: &impl Serialize,
    ) -> Result<T> {
        check_name("collection", collection)?;
        self.management(
            Method::PUT,
            &["auto-index", collection],
            &[],
            Some(body),
            StatusCode::OK,
        )
        .await
    }
    /// `GET D/index-recommendations`.
    pub async fn list_index_recommendations<T: DeserializeOwned>(&self) -> Result<T> {
        self.management(
            Method::GET,
            &["index-recommendations"],
            &[],
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
    /// `POST D/index-recommendations/{recommendation}/apply`; creation or existing index.
    pub async fn apply_index_recommendation<T: DeserializeOwned>(
        &self,
        recommendation: &str,
    ) -> Result<T> {
        check_database_selector(self.name())?;
        if recommendation.is_empty() {
            return Err(Error::InvalidInput(
                "recommendation must be nonempty".into(),
            ));
        }
        self.client()
            .send_project(Req::new(
                Method::POST,
                vec![
                    "v1",
                    "databases",
                    self.name(),
                    "index-recommendations",
                    recommendation,
                    "apply",
                ],
            ))
            .await?
            .decode_status(&[StatusCode::OK, StatusCode::CREATED])
    }
}
impl Collection {
    /// `GET C/schema`; the explicit data-plane validation schema.
    pub async fn get_schema<T: DeserializeOwned>(&self) -> Result<T> {
        check_name("collection", self.name())?;
        self.database()
            .management(
                Method::GET,
                &["collections", self.name(), "schema"],
                &[],
                None::<&()>,
                StatusCode::OK,
            )
            .await
    }
    /// `PUT C/schema`; JSON Schema body, distinct from management inference.
    pub async fn put_schema<T: DeserializeOwned>(&self, body: &impl Serialize) -> Result<T> {
        check_name("collection", self.name())?;
        self.database()
            .management(
                Method::PUT,
                &["collections", self.name(), "schema"],
                &[],
                Some(body),
                StatusCode::OK,
            )
            .await
    }
    /// `DELETE C/schema`; removes the explicit validation schema.
    pub async fn delete_schema<T: DeserializeOwned>(&self) -> Result<T> {
        check_name("collection", self.name())?;
        self.database()
            .management(
                Method::DELETE,
                &["collections", self.name(), "schema"],
                &[],
                None::<&()>,
                StatusCode::OK,
            )
            .await
    }
}
