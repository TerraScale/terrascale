ALTER TABLE deletion_cleanup ADD COLUMN targets_complete INTEGER NOT NULL DEFAULT 1 CHECK (targets_complete IN (0, 1));
