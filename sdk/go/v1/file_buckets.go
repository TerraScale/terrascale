package v1

import (
	"context"
	"errors"
	"net/http"
	"net/url"
)

// FileAccessPolicy grants one file operation to end users.
type FileAccessPolicy struct {
	Owner  bool     `json:"owner"`
	Public bool     `json:"public"`
	Roles  []string `json:"roles"`
}

// FileAccessRules is a file bucket's declarative end-user permissions; missing grants deny access.
type FileAccessRules struct {
	Owner *string           `json:"owner"`
	Read  FileAccessPolicy `json:"read"`
	Write FileAccessPolicy `json:"write"`
}

// FileBucket is a project file bucket and its end-user policy.
type FileBucket struct {
	ID     string          `json:"id" ulid:"required"`
	Name   string          `json:"name"`
	Region string          `json:"region"`
	Rules  FileAccessRules `json:"rules"`
}

type createFileBucketRequest struct {
	Name  string          `json:"name"`
	Rules FileAccessRules `json:"rules"`
}

// ListBuckets lists a project's file buckets with sql:read, one bounded page in ID order.
func (c *Client) ListBuckets(ctx context.Context, project string, opts PageOptions) (*Page[FileBucket], error) {
	req, err := projectRequest(http.MethodGet, project, bucketAfterQuery(opts.After), "buckets")
	if err != nil {
		return nil, err
	}
	var out Page[FileBucket]
	if _, err := c.call(ctx, req, nil, &out); err != nil {
		return nil, err
	}
	if out.Items == nil {
		return nil, errors.New("terrabase: buckets response requires items array")
	}
	return &out, nil
}

// CreateBucket creates a project file bucket with sql:write; rules deny end users unless granted.
func (c *Client) CreateBucket(ctx context.Context, project, name string, rules FileAccessRules) (*FileBucket, error) {
	if err := checkName("bucket", name); err != nil {
		return nil, err
	}
	return projectCall[FileBucket](ctx, c, http.MethodPost, project, nil, createFileBucketRequest{Name: name, Rules: rules}, "buckets")
}

// DeleteBucket deletes an empty file bucket by its immutable ULID with sql:write.
func (c *Client) DeleteBucket(ctx context.Context, project, bucketID string) error {
	if err := checkID(bucketID); err != nil {
		return err
	}
	req, err := projectRequest(http.MethodDelete, project, nil, "buckets", bucketID)
	if err != nil {
		return err
	}
	_, err = c.call(ctx, req, nil, nil)
	return err
}

func bucketAfterQuery(after string) url.Values {
	q := url.Values{}
	if after != "" {
		q.Set("after", after)
	}
	return q
}
