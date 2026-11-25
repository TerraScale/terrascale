UPDATE api_keys SET expires_at = created_at
WHERE instr(secret_bindings, '"credential:user-state"') > 0
  AND (expires_at IS NULL OR expires_at > created_at)
  AND id NOT IN (
    SELECT k.id FROM api_keys k JOIN users u ON u.id = k.created_by AND u.instance_id = k.instance_id
    WHERE instr(k.secret_bindings, '{"kind":"credential:user-state","key":"' || u.id || '","ownerId":"' || u.auth_stamp || '"}') > 0
      AND length(k.secret_bindings) - length(replace(k.secret_bindings, '"credential:user-state"', '')) = length('"credential:user-state"')
  );

UPDATE api_keys SET previous_secret_hash = NULL, previous_secret_expires_at = NULL, previous_secret_bindings = '[]'
WHERE instr(previous_secret_bindings, '"credential:user-state"') > 0
  AND id NOT IN (
    SELECT k.id FROM api_keys k JOIN users u ON u.id = k.created_by AND u.instance_id = k.instance_id
    WHERE instr(k.previous_secret_bindings, '{"kind":"credential:user-state","key":"' || u.id || '","ownerId":"' || u.auth_stamp || '"}') > 0
      AND length(k.previous_secret_bindings) - length(replace(k.previous_secret_bindings, '"credential:user-state"', '')) = length('"credential:user-state"')
  );

UPDATE api_keys SET expires_at = created_at
WHERE (expires_at IS NULL OR expires_at > created_at)
  AND id IN (
    SELECT k.id FROM api_keys k LEFT JOIN memberships m ON m.organization_id = k.organization_id AND m.user_id = k.created_by
    WHERE m.user_id IS NULL OR k.project_id IS NULL AND (m.role IS NULL OR m.role NOT IN ('admin', 'owner'))
  );
