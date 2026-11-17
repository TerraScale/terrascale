use super::*;
use std::collections::BTreeMap;

#[derive(Clone, Deserialize)]
pub struct CustomToken {
    pub access_token: String,
    pub token_type: String,
    pub expires_in: u64,
}

impl std::fmt::Debug for CustomToken {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("CustomToken")
            .field("access_token", &"[REDACTED]")
            .field("token_type", &self.token_type)
            .field("expires_in", &self.expires_in)
            .finish()
    }
}

impl ProjectsClient {
    pub async fn mint_custom_token(
        &self,
        project_key: &str,
        project: &str,
        user_id: &str,
        database: Option<&str>,
        claims: Option<&BTreeMap<String, Value>>,
    ) -> Result<CustomToken> {
        path_id(user_id, "user_id")?;
        let mut body = serde_json::json!({"user_id": user_id});
        if let Some(database) = database {
            path_id(database, "database")?;
            body["database"] = Value::String(database.to_owned());
        }
        if let Some(claims) = claims {
            body["claims"] = serde_json::to_value(claims).map_err(|_| {
                Error::new(
                    ErrorKind::Validation,
                    "invalid_claims",
                    "Claims must be JSON values.",
                )
            })?;
        }
        let token: CustomToken = self
            .send_typed(
                "auth_projects.mint_custom_token",
                Method::Post,
                &format!("/{}/custom-tokens", self.project(project)?),
                Some(&body),
                project_key,
            )
            .await?;
        if token.access_token.is_empty()
            || token.token_type != "Bearer"
            || !(1..=300).contains(&token.expires_in)
        {
            return Err(Error::new(
                ErrorKind::InvalidResponse,
                "invalid_custom_token",
                "Invalid custom token response.",
            ));
        }
        Ok(token)
    }
}
