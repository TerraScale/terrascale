pub mod models;

#[cfg(feature = "axum-response")]
use axum::{
    Json,
    response::{IntoResponse, Response},
};
use http::StatusCode;
#[cfg(feature = "axum-response")]
use serde::Serialize;

pub type AppResult<T> = Result<T, AppError>;

#[derive(Debug, Clone)]
pub struct AppError {
    pub status: StatusCode,
    pub code: String,
    pub message: String,
}

#[cfg(feature = "axum-response")]
#[derive(Serialize)]
struct ErrorBody<'a> {
    error: ErrorDetail<'a>,
}

#[cfg(feature = "axum-response")]
#[derive(Serialize)]
struct ErrorDetail<'a> {
    code: &'a str,
    message: &'a str,
}

impl AppError {
    pub fn new(status: StatusCode, code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            status,
            code: code.into(),
            message: message.into(),
        }
    }

    #[must_use]
    pub fn internal() -> Self {
        Self::new(
            StatusCode::INTERNAL_SERVER_ERROR,
            "internal_error",
            "An unexpected error occurred.",
        )
    }

    #[must_use]
    pub fn unavailable() -> Self {
        Self::new(
            StatusCode::SERVICE_UNAVAILABLE,
            "temporarily_unavailable",
            "The storage engine is unavailable; retry shortly.",
        )
    }
}

#[cfg(feature = "axum-response")]
impl IntoResponse for AppError {
    fn into_response(self) -> Response {
        (
            self.status,
            Json(ErrorBody {
                error: ErrorDetail {
                    code: &self.code,
                    message: &self.message,
                },
            }),
        )
            .into_response()
    }
}
