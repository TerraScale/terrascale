package terrabase

import (
	"encoding/json"
	"fmt"
	"sort"
	"strconv"
	"strings"

	"terrascale.tech/dashboard/internal/core"
)

// Phase 1 keeps the local resource features in Documents. Each document,
// history revision, schema, index set, and backup item has its own record;
// database roots never accumulate the simulated data plane's payloads.
func encodeDatabase(records map[coreKey]coreRecord, orgID, projectID string, db core.Database) error {
	put := func(key string, data any, r coreRecord) error {
		r.Kind, r.OrgID, r.ProjectID, r.DatabaseID = "record", orgID, projectID, db.ID
		r.ID = recordKey("databases", key).id
		var err error
		r.Data, err = encodeData(data)
		if err != nil {
			return core.ErrInvalid
		}
		k := recordKey("databases", key)
		if _, exists := records[k]; exists {
			return core.ErrConflict
		}
		records[k] = r
		return nil
	}
	fields := db
	fields.Documents, fields.DocumentHistory, fields.Schemas, fields.Indexes, fields.Backups = nil, nil, nil, nil, nil
	if err := put(db.ID, fields, coreRecord{RecordType: "database", Name: db.Name, NameKey: strings.ToLower(strings.TrimSpace(db.Name)), CreatedAt: db.CreatedAt}); err != nil {
		return err
	}
	payload := func(kind, backupID, collection, id string, position int, data any) error {
		parts, _ := json.Marshal([]string{db.ID, kind, backupID, collection, id, strconv.Itoa(position)})
		key := "payload:" + string(parts)
		if kind == "backup" {
			key = backupID
		}
		return put(key, data, coreRecord{RecordType: kind, BackupID: backupID, CollectionName: collection, ResourceID: id, Position: position})
	}
	contents := func(backupID string, documents map[string]map[string][]byte, schemas map[string][]byte, indexes map[string][]string) error {
		for collection, docs := range documents {
			if err := payload("collection", backupID, collection, "", 0, struct{}{}); err != nil {
				return err
			}
			for id, data := range docs {
				if err := payload("document", backupID, collection, id, 0, data); err != nil {
					return err
				}
			}
		}
		for collection, data := range schemas {
			if err := payload("schema", backupID, collection, "", 0, data); err != nil {
				return err
			}
		}
		for collection, data := range indexes {
			if err := payload("indexes", backupID, collection, "", 0, data); err != nil {
				return err
			}
		}
		return nil
	}
	if err := contents("", db.Documents, db.Schemas, db.Indexes); err != nil {
		return err
	}
	for collection, docs := range db.DocumentHistory {
		for id, revisions := range docs {
			for position, data := range revisions {
				if err := payload("history", "", collection, id, position, data); err != nil {
					return err
				}
			}
		}
	}
	seen := make(map[string]bool)
	for position, backup := range db.Backups {
		if backup.ID == "" || seen[backup.ID] || backup.DatabaseID != db.ID {
			return core.ErrInvalid
		}
		seen[backup.ID] = true
		fields := backup
		fields.Documents, fields.Schemas, fields.Indexes = nil, nil, nil
		if err := payload("backup", backup.ID, "", "", position, fields); err != nil {
			return err
		}
		if err := contents(backup.ID, backup.Documents, backup.Schemas, backup.Indexes); err != nil {
			return err
		}
	}
	return nil
}

func materializeDatabases(org *core.Organization, records []coreRecord) error {
	for _, r := range records {
		if r.RecordType != "" && r.RecordType != "database" {
			continue
		}
		db, err := decodeData[core.Database](r)
		if err != nil {
			return err
		}
		project, exists := org.Projects[db.ProjectID]
		if !exists {
			return errCoreChanged
		}
		db.Documents, db.DocumentHistory = make(map[string]map[string][]byte), make(map[string]map[string][][]byte)
		db.Schemas, db.Indexes = make(map[string][]byte), make(map[string][]string)
		project.Databases[db.ID] = db
	}
	backups := make(map[string]map[string]core.Backup)
	positions := make(map[string]map[string]int)
	for _, r := range records {
		if r.RecordType == "backup" {
			backup, err := decodeData[core.Backup](r)
			if err != nil {
				return err
			}
			if backups[r.DatabaseID] == nil {
				backups[r.DatabaseID] = make(map[string]core.Backup)
				positions[r.DatabaseID] = make(map[string]int)
			}
			backup.Documents = make(map[string]map[string][]byte)
			backup.Schemas, backup.Indexes = make(map[string][]byte), make(map[string][]string)
			backups[r.DatabaseID][backup.ID] = backup
			positions[r.DatabaseID][backup.ID] = r.Position
		}
	}
	for _, r := range records {
		if r.RecordType == "" || r.RecordType == "database" || r.RecordType == "backup" {
			continue
		}
		project, exists := org.Projects[r.ProjectID]
		if !exists {
			return errCoreChanged
		}
		db, exists := project.Databases[r.DatabaseID]
		if !exists {
			return errCoreChanged
		}
		documents, schemas, indexes := db.Documents, db.Schemas, db.Indexes
		if r.BackupID != "" {
			backup, exists := backups[r.DatabaseID][r.BackupID]
			if !exists {
				return errCoreChanged
			}
			documents, schemas, indexes = backup.Documents, backup.Schemas, backup.Indexes
		}
		switch r.RecordType {
		case "collection":
			if documents[r.CollectionName] == nil {
				documents[r.CollectionName] = make(map[string][]byte)
			}
		case "document":
			data, err := decodeData[[]byte](r)
			if err != nil {
				return err
			}
			if documents[r.CollectionName] == nil {
				documents[r.CollectionName] = make(map[string][]byte)
			}
			documents[r.CollectionName][r.ResourceID] = data
		case "schema":
			data, err := decodeData[[]byte](r)
			if err != nil {
				return err
			}
			schemas[r.CollectionName] = data
		case "indexes":
			data, err := decodeData[[]string](r)
			if err != nil {
				return err
			}
			indexes[r.CollectionName] = data
		case "history":
			data, err := decodeData[[]byte](r)
			if err != nil {
				return err
			}
			if r.Position < 0 || r.Position > 10000 {
				return coreUnavailable(fmt.Errorf("invalid history position %d", r.Position))
			}
			if db.DocumentHistory[r.CollectionName] == nil {
				db.DocumentHistory[r.CollectionName] = make(map[string][][]byte)
			}
			revisions := db.DocumentHistory[r.CollectionName][r.ResourceID]
			if len(revisions) <= r.Position {
				revisions = append(revisions, make([][]byte, r.Position+1-len(revisions))...)
			}
			revisions[r.Position] = data
			db.DocumentHistory[r.CollectionName][r.ResourceID] = revisions
		default:
			return coreUnavailable(fmt.Errorf("unknown database payload %q", r.RecordType))
		}
		project.Databases[db.ID] = db
	}
	for _, project := range org.Projects {
		for id, db := range project.Databases {
			ids := make([]string, 0, len(backups[id]))
			for backupID := range backups[id] {
				ids = append(ids, backupID)
			}
			sort.Slice(ids, func(i, j int) bool { return positions[id][ids[i]] < positions[id][ids[j]] })
			for _, backupID := range ids {
				db.Backups = append(db.Backups, backups[id][backupID])
			}
			project.Databases[id] = db
		}
		org.Projects[project.ID] = project
	}
	return nil
}
