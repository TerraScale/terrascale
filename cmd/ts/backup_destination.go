package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"

	v1 "terrascale.tech/sdk/v1"
)

// Read the full credential-bearing document only from stdin. Decoder errors
// can quote values, so none of their text or input bytes are returned to callers.
func readBackupDestination(in io.Reader) (v1.ProjectBackupDestinationRequest, error) {
	var req v1.ProjectBackupDestinationRequest
	data, err := io.ReadAll(io.LimitReader(in, (16<<10)+1))
	if err != nil {
		return req, errors.New("could not read backup destination JSON from stdin")
	}
	invalid := fmt.Errorf("stdin must contain one backup destination JSON object (endpoint, region, bucket, mode, accessKeyId, secretKey), at most 16 KiB: %w", errUsage)
	if len(data) > 16<<10 {
		return req, invalid
	}
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.DisallowUnknownFields()
	if dec.Decode(&req) != nil || dec.Decode(new(any)) != io.EOF || req.Endpoint == "" || req.Region == "" || req.Bucket == "" ||
		req.AccessKeyID == "" || req.SecretKey == "" || req.Mode != "both" && req.Mode != "customer" {
		return v1.ProjectBackupDestinationRequest{}, invalid
	}
	return req, nil
}

// A destination request contains credentials. Keep API classification for JSON
// errors, but discard producer messages/details and arbitrary transport errors.
func destinationError(err error) error {
	if errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		return err
	}
	var api *v1.APIError
	if errors.As(err, &api) {
		return &v1.APIError{Status: api.Status, Code: api.Code, RequestID: api.RequestID,
			Message: "Backup destination request failed; inspect permissions, endpoint and credentials before retrying."}
	}
	return errors.New("backup destination request failed; inspect permissions, endpoint and credentials before retrying")
}

func backupDestination(ctx context.Context, tb *v1.Client, project, operation string, in io.Reader, consent managementConsent, out io.Writer) error {
	switch operation {
	case "get":
		result, err := tb.BackupDestination(ctx, project)
		if errors.Is(err, v1.ErrNotFound) {
			return &cliError{code: "not_found", status: http.StatusNotFound,
				message: "no backup destination is set for this project; ts backup destination set configures one"}
		}
		if err != nil {
			return destinationError(err)
		}
		return emitJSON(out, result)
	case "set":
		if err := consent("backup destination", project, project); err != nil {
			return err
		}
		req, err := readBackupDestination(in)
		if err != nil {
			return err
		}
		result, err := tb.SetBackupDestination(ctx, project, req)
		if err != nil {
			return destinationError(err)
		}
		return emitJSON(out, result) // Only public SDK metadata, never req.
	case "delete":
		if err := consent("backup destination", project, project); err != nil {
			return err
		}
		if err := tb.DeleteBackupDestination(ctx, project); err != nil {
			return destinationError(err)
		}
		return emitJSON(out, struct {
			Deleted   bool   `json:"deleted"`
			ProjectID string `json:"projectId"`
		}{true, project})
	default:
		return fmt.Errorf("backup destination operation must be get, set or delete: %w", errUsage)
	}
}
