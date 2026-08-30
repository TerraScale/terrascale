# S3 backup and restore

TerraBase writes verified recovery points to an S3-compatible bucket. The
bucket is the durable copy. Studio uses local disk only for disposable upload
or restore scratch space.

## Bucket setup

Create a bucket in the backup Region and give the TerraBase workload these
permissions on one database-specific prefix:

```text
s3:ListBucket
s3:GetObject
s3:PutObject
s3:PutObjectTagging
```

Do not grant `s3:DeleteObject` to the normal backup workload. Configure these
controls on the bucket:

- block public access;
- versioning;
- default encryption, preferably KMS for production;
- object lock when the compliance policy requires it;
- lifecycle cleanup for abandoned multipart uploads;
- lifecycle expiration filtered by the `terrabase-kind` object tag. Keep
  `incremental` objects for the required restore window. Keep `full` objects
  for that window plus at least the configured full-point interval, rounded
  up to the provider's lifecycle granularity. For example, a 35-day restore
  window with a 24-hour full interval uses at least 35 days for incrementals
  and 36 days for full objects.

Use a different prefix for every database. Mixing databases in one prefix
fails closed because each manifest binds its database and partition.

## Credentials

Prefer workload identity, an ECS task role, or an EC2 instance role. Static
credentials use the standard variables:

```sh
export AWS_ACCESS_KEY_ID='...'
export AWS_SECRET_ACCESS_KEY='...'
export AWS_SESSION_TOKEN='...'
```

Studio does not save these values. Its sidecar stores only the bucket, Region,
prefix, endpoint, intervals, request style, and KMS key id.

## Configure in Studio

Start Studio on loopback and open `/backups`. Enter the bucket, signing Region,
and a database-specific prefix. For AWS S3, leave the endpoint blank. For an
S3-compatible provider, enter its HTTPS endpoint and choose the request style
it requires.

Choose the incremental interval and the full-point interval. Five minutes and
twenty-four hours are the defaults. Select "Save and test S3". Studio lists the
prefix and validates every manifest before it saves the settings.

Deployments may configure the same values without the form:

```sh
export TERRABASE_BACKUP_ENABLED=true
export TERRABASE_BACKUP_S3_BUCKET=terrabase-backups
export TERRABASE_BACKUP_S3_REGION=us-east-1
export TERRABASE_BACKUP_S3_PREFIX=production/database-01
export TERRABASE_BACKUP_INCREMENTAL_MINUTES=5
export TERRABASE_BACKUP_FULL_HOURS=24
```

Optional settings are:

```sh
export TERRABASE_BACKUP_S3_ENDPOINT=https://s3.example.com
export TERRABASE_BACKUP_S3_VIRTUAL_HOSTED_STYLE=false
export TERRABASE_BACKUP_S3_KMS_KEY_ID=alias/terrabase-backups
```

`TERRABASE_BACKUP_S3_ALLOW_HTTP=true` exists only for a loopback development
store. Production endpoints must use HTTPS.

## Run and verify

"Back up now" creates an incremental recovery point when a full root exists
and the full interval has not expired. "Create full point" forces a new
self-contained point. Members upload concurrently. The manifest uploads last,
so listing never presents an incomplete recovery point as committed.
Studio tags every object in a full or snapshot point with
`terrabase-kind=full` and every incremental object with
`terrabase-kind=incremental`. The extra full-point retention keeps the root
available for every incremental inside the promised restore window.

Use "Verify the chain" after setup changes and during recovery drills. It
downloads the selected chain, checks every bound length and SHA-256 digest,
and removes the transfer scratch directory when it finishes.

Monitor:

```text
terrabase_backup_scheduler_enabled
terrabase_backup_runs_total{outcome="success"}
terrabase_backup_runs_total{outcome="failure"}
terrabase_backup_last_success_timestamp_seconds
```

Alert when the last-success time is older than twice the configured
incremental interval. Studio metrics cover only the Studio process. Production
uses the private engine registry described by ADR 0036.

## Restore

On `/backups`, leave the identity blank for the newest point or paste a full
recovery-point identity. Choose a new, empty target directory. Restore performs
these steps:

1. List and validate the manifests under the configured prefix.
2. Resolve the selected full-plus-incremental chain.
3. Download only that chain into process-unique scratch space.
4. Verify every member before replay.
5. Replay into a new engine location and reopen it for read-back checks.
6. Remove the downloaded scratch data.

Studio does not switch the running engine to the restored directory. Inspect
the result by starting a separate Studio with `TERRABASE_STUDIO_DIR` set to the
restored location. Production restore creates a new cluster and moves traffic
only after topology, key, and frontier checks pass.

Never restore into the active engine directory. Never run a second process
that opens the active directory for backup. The S3 adapter must receive the
live in-process `Engine` owned by the host.
