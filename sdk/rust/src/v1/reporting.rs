//! Saved SQL and usage reporting. Responses are generic to retain complete JSON.
use super::{
    client::{Client, Req},
    documents::Database,
    errors::{Error, Result},
    ids::valid_entity_id,
};
use reqwest::{Method, StatusCode};
use serde::{de::DeserializeOwned, Serialize};

fn saved_query_id(id: &str) -> Result<()> {
    if valid_entity_id(id) {
        Ok(())
    } else {
        Err(Error::InvalidInput(
            "saved query must be a canonical uppercase ULID".into(),
        ))
    }
}
impl Client {
    /// `GET P/usage`; query accepts granularity, from and to.
    pub async fn get_project_usage<T: DeserializeOwned>(
        &self,
        query: &[(&str, String)],
    ) -> Result<T> {
        self.management(
            Method::GET,
            vec!["v1", "usage"],
            query,
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
    /// `GET P/analytics`; query selects the window, region and database.
    pub async fn get_project_analytics<T: DeserializeOwned>(
        &self,
        query: &[(&str, String)],
    ) -> Result<T> {
        self.management(
            Method::GET,
            vec!["v1", "analytics"],
            query,
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
    /// `GET P/replication`; query selects the window, region and database.
    pub async fn get_project_replication<T: DeserializeOwned>(
        &self,
        query: &[(&str, String)],
    ) -> Result<T> {
        self.management(
            Method::GET,
            vec!["v1", "replication"],
            query,
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
    /// `POST P/saved-queries`; body contains name and sql.
    pub async fn create_saved_query<T: DeserializeOwned>(
        &self,
        body: &impl Serialize,
    ) -> Result<T> {
        self.management(
            Method::POST,
            vec!["v1", "saved-queries"],
            &[],
            Some(body),
            StatusCode::CREATED,
        )
        .await
    }
    /// `GET P/saved-queries`; management after/next pagination.
    pub async fn list_saved_queries<T: DeserializeOwned>(
        &self,
        query: &[(&str, String)],
    ) -> Result<T> {
        self.management(
            Method::GET,
            vec!["v1", "saved-queries"],
            query,
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
    /// `GET P/saved-queries/{query}`.
    pub async fn get_saved_query<T: DeserializeOwned>(&self, id: &str) -> Result<T> {
        saved_query_id(id)?;
        self.management(
            Method::GET,
            vec!["v1", "saved-queries", id],
            &[],
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
    /// `PUT P/saved-queries/{query}`; body contains name and sql.
    pub async fn update_saved_query<T: DeserializeOwned>(
        &self,
        id: &str,
        body: &impl Serialize,
    ) -> Result<T> {
        saved_query_id(id)?;
        self.management(
            Method::PUT,
            vec!["v1", "saved-queries", id],
            &[],
            Some(body),
            StatusCode::OK,
        )
        .await
    }
    /// `DELETE P/saved-queries/{query}`.
    pub async fn delete_saved_query(&self, id: &str) -> Result<()> {
        saved_query_id(id)?;
        self.send_project(Req::new(Method::DELETE, vec!["v1", "saved-queries", id]))
            .await?
            .check_status(&[StatusCode::NO_CONTENT])
    }
}
impl Database {
    /// `GET D/usage`; query accepts granularity, from and to.
    pub async fn get_database_usage<T: DeserializeOwned>(
        &self,
        query: &[(&str, String)],
    ) -> Result<T> {
        self.management(Method::GET, &["usage"], query, None::<&()>, StatusCode::OK)
            .await
    }
    /// `GET D/analytics`; database selector is a name.
    pub async fn get_database_analytics<T: DeserializeOwned>(
        &self,
        query: &[(&str, String)],
    ) -> Result<T> {
        self.management(
            Method::GET,
            &["analytics"],
            query,
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
    /// `GET D/replication`; database selector is a name.
    pub async fn get_database_replication<T: DeserializeOwned>(
        &self,
        query: &[(&str, String)],
    ) -> Result<T> {
        self.management(
            Method::GET,
            &["replication"],
            query,
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
    /// `GET D/query-shapes`; bounded query observations, not a SQL execution.
    pub async fn list_query_shapes<T: DeserializeOwned>(&self) -> Result<T> {
        self.management(
            Method::GET,
            &["query-shapes"],
            &[],
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
}
