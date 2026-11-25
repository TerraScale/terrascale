CREATE TABLE project_reservations (
    id TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    project_id TEXT,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    CONSTRAINT pk_project_reservations PRIMARY KEY (id),
    CONSTRAINT fk_project_reservations_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE CASCADE,
    CONSTRAINT ck_project_reservations_id CHECK (length(id) = 26),
    CONSTRAINT ck_project_reservations_project_id CHECK (project_id IS NULL OR length(project_id) = 26)
);
CREATE INDEX ix_project_reservations_organization_id_expires_at ON project_reservations (organization_id, expires_at);
