//! Shared end-user access policy for collections and file buckets.
use reqwest::{Method, StatusCode};
use serde::{Deserialize, Serialize};

use super::{client::Req, Collection, Result};

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

/// Declarative end-user policy; project credentials manage this policy.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct CollectionRules {
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

#[derive(Deserialize)]
struct RulesBody {
    // Option normally accepts an omitted field. The wrapper requires it:
    // only an explicit null represents undeclared, denying rules.
    #[serde(deserialize_with = "Option::deserialize")]
    rules: Option<CollectionRules>,
}

impl Collection {
    /// Returns the policy, or `None` before rules are declared (end users denied).
    pub async fn get_rules(&self) -> Result<Option<CollectionRules>> {
        self.check()?;
        let response = self
            .client()
            .send_project(Req::new(Method::GET, self.segments(&["rules"])))
            .await?;
        Ok(response
            .decode_status::<RulesBody>(&[StatusCode::OK])?
            .rules)
    }

    /// Replaces the entire policy. Both grant objects and roles arrays are sent.
    /// End-user writes cannot transfer ownership or steal another user's deleted ID.
    pub async fn put_rules(&self, rules: &CollectionRules) -> Result<Option<CollectionRules>> {
        self.check()?;
        let request = Req::new(Method::PUT, self.segments(&["rules"])).json(rules)?;
        let response = self.client().send_project(request).await?;
        Ok(response
            .decode_status::<RulesBody>(&[StatusCode::OK])?
            .rules)
    }
}
