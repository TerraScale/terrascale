//! One-time source imports. Uploads send bytes, never JSON/base64 wrappers.
use super::{
    client::{Req, MAX_REQUEST_BYTES},
    documents::Database,
    errors::{Error, Result},
    ids::{check_database_selector, valid_entity_id},
};
use reqwest::{Method, StatusCode};
use serde::{de::DeserializeOwned, Serialize};

impl Database {
    /// `POST D/import`; 201 new job or 200 same source already registered.
    pub async fn create_import<T: DeserializeOwned>(&self, body: &impl Serialize) -> Result<T> {
        check_database_selector(self.name())?;
        let req =
            Req::new(Method::POST, vec!["v1", "databases", self.name(), "import"]).json(body)?;
        self.client()
            .send_project(req)
            .await?
            .decode_status(&[StatusCode::OK, StatusCode::CREATED])
    }
    /// `GET D/import`; retains nested preview, progress, validation and errors.
    pub async fn get_import<T: DeserializeOwned>(&self) -> Result<T> {
        self.management(Method::GET, &["import"], &[], None::<&()>, StatusCode::OK)
            .await
    }
    /// `PATCH D/import`; body contains the updated options.
    pub async fn update_import<T: DeserializeOwned>(&self, body: &impl Serialize) -> Result<T> {
        self.management(
            Method::PATCH,
            &["import"],
            &[],
            Some(body),
            StatusCode::ACCEPTED,
        )
        .await
    }
    /// `PUT D/import/source?offset=`; one nonempty part, up to 4 MiB.
    pub async fn upload_import_source<T: DeserializeOwned>(
        &self,
        offset: u64,
        bytes: &[u8],
    ) -> Result<T> {
        check_database_selector(self.name())?;
        if offset > i64::MAX as u64 || bytes.is_empty() || bytes.len() > MAX_REQUEST_BYTES {
            return Err(Error::InvalidInput(
                "import offset must fit int64 and part must contain 1 to 4194304 bytes".into(),
            ));
        }
        let mut req = Req::new(
            Method::PUT,
            vec!["v1", "databases", self.name(), "import", "source"],
        );
        req.query.push(("offset", offset.to_string()));
        req.content_type = Some("application/octet-stream");
        req.body = Some(bytes.to_vec());
        self.client()
            .send_project(req)
            .await?
            .decode_status(&[StatusCode::OK])
    }
    /// `POST D/import/start`; only the preview the caller explicitly confirmed.
    pub async fn start_import<T: DeserializeOwned>(&self, preview_id: &str) -> Result<T> {
        if !valid_entity_id(preview_id) {
            return Err(Error::InvalidInput(
                "preview ID must be a canonical uppercase ULID".into(),
            ));
        }
        self.management(
            Method::POST,
            &["import", "start"],
            &[],
            Some(&serde_json::json!({"previewId":preview_id})),
            StatusCode::ACCEPTED,
        )
        .await
    }
    /// `POST D/import/cancel`; asynchronous cancellation (202).
    pub async fn cancel_import<T: DeserializeOwned>(&self) -> Result<T> {
        self.management(
            Method::POST,
            &["import", "cancel"],
            &[],
            None::<&()>,
            StatusCode::ACCEPTED,
        )
        .await
    }
    /// `POST D/import/resume`; asynchronous resume (202).
    pub async fn resume_import<T: DeserializeOwned>(&self) -> Result<T> {
        self.management(
            Method::POST,
            &["import", "resume"],
            &[],
            None::<&()>,
            StatusCode::ACCEPTED,
        )
        .await
    }
}
