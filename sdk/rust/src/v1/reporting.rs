//! Usage reporting. Responses are generic to retain complete JSON.
use super::{client::Client, database::Database, errors::Result};
use serde::de::DeserializeOwned;
impl Client {
    /// `GET P/usage`; query accepts granularity, from and to.
    pub async fn get_project_usage<T: DeserializeOwned>(
        &self,
        query: &[(&str, String)],
    ) -> Result<T> {
        self.get_json(vec!["v1", "usage"], query).await
    }
    /// `GET P/analytics`; query selects the window, region and database.
    pub async fn get_project_analytics<T: DeserializeOwned>(
        &self,
        query: &[(&str, String)],
    ) -> Result<T> {
        self.get_json(vec!["v1", "analytics"], query).await
    }
}
impl Database {
    /// `GET D/usage`; query accepts granularity, from and to.
    pub async fn get_database_usage<T: DeserializeOwned>(
        &self,
        query: &[(&str, String)],
    ) -> Result<T> {
        self.get_json(&["usage"], query).await
    }
    /// `GET D/analytics`; database selector is a name.
    pub async fn get_database_analytics<T: DeserializeOwned>(
        &self,
        query: &[(&str, String)],
    ) -> Result<T> {
        self.get_json(&["analytics"], query).await
    }
}
