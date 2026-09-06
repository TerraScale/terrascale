//! Private lifecycle control RPCs.
//!
//! The service is wired into the real TBEP listener so callers cannot stop at
//! an unused adapter. Native lifecycle composition is intentionally explicit;
//! until supplied, validated requests receive a typed unavailable response.

use tonic::{Code, Request, Response, Status};

use super::{TbepGrpcServer, authenticate_gateway_certificate, metadata_value};
use crate::lifecycle;
use crate::proto::terrabase::v1 as pb;
use pb::lifecycle_control_service_server::{
    LifecycleControlService, LifecycleControlServiceServer,
};

impl TbepGrpcServer {
    fn authenticate_lifecycle<T>(&self, request: &Request<T>) -> Result<(), Status> {
        let principal = metadata_value(
            request.metadata(),
            super::PRINCIPAL_METADATA,
            super::MAX_PRINCIPAL_BYTES,
        )?;
        if !self.config.gateway_principals.contains(&principal) {
            return Err(Status::new(
                Code::PermissionDenied,
                "principal is not an admitted orchestrator",
            ));
        }
        let certificates = request
            .peer_certs()
            .ok_or_else(|| Status::new(Code::Unauthenticated, "lifecycle control requires mTLS"))?;
        let leaf = certificates
            .first()
            .ok_or_else(|| Status::new(Code::Unauthenticated, "lifecycle control requires mTLS"))?;
        authenticate_gateway_certificate(&principal, leaf)
    }
}

#[tonic::async_trait]
impl LifecycleControlService for std::sync::Arc<TbepGrpcServer> {
    async fn ensure_snapshot(
        &self,
        request: Request<pb::LifecycleControlRequest>,
    ) -> Result<Response<pb::LifecycleControlResponse>, Status> {
        self.dispatch_unavailable(request)
    }

    async fn verify_snapshot_placement(
        &self,
        request: Request<pb::LifecycleControlRequest>,
    ) -> Result<Response<pb::LifecycleControlResponse>, Status> {
        self.dispatch_unavailable(request)
    }

    async fn close_database(
        &self,
        request: Request<pb::LifecycleControlRequest>,
    ) -> Result<Response<pb::LifecycleControlResponse>, Status> {
        self.dispatch_unavailable(request)
    }

    async fn stop_native_backup(
        &self,
        request: Request<pb::LifecycleControlRequest>,
    ) -> Result<Response<pb::LifecycleControlResponse>, Status> {
        self.dispatch_unavailable(request)
    }

    async fn reactivate(
        &self,
        request: Request<pb::LifecycleReactivateRequest>,
    ) -> Result<Response<pb::LifecycleControlResponse>, Status> {
        self.authenticate_lifecycle(&request)?;
        let request = request.into_inner();
        let operation = lifecycle::validate_reactivate(&request).map_err(|_| {
            Status::new(Code::InvalidArgument, "lifecycle restore target is invalid")
        })?;
        Ok(Response::new(lifecycle::unavailable(operation)))
    }

    async fn delete_owned_data(
        &self,
        request: Request<pb::LifecycleControlRequest>,
    ) -> Result<Response<pb::LifecycleControlResponse>, Status> {
        self.dispatch_unavailable(request)
    }
}

impl TbepGrpcServer {
    async fn dispatch_unavailable(
        &self,
        request: Request<pb::LifecycleControlRequest>,
    ) -> Result<Response<pb::LifecycleControlResponse>, Status> {
        self.authenticate_lifecycle(&request)?;
        let request = request.into_inner();
        let operation_id = lifecycle::validate_request(&request)
            .map_err(|_| Status::new(Code::InvalidArgument, "lifecycle identity is invalid"))?;
        Ok(Response::new(lifecycle::unavailable(operation_id)))
    }
}

/// Registers the lifecycle service on a TBEP router.
pub fn server(
    server: std::sync::Arc<TbepGrpcServer>,
) -> LifecycleControlServiceServer<std::sync::Arc<TbepGrpcServer>> {
    LifecycleControlServiceServer::new(server)
}
