UPDATE api_keys SET secret_hash = 'retired:' || id, secret_bindings = '[]', version = version || '.k2'
WHERE instr(secret_bindings, '"credential:user-state"') > 0
  AND id NOT IN (SELECT k.id FROM api_keys k JOIN users u ON u.instance_id = k.instance_id AND u.id = substr(substr(k.secret_bindings, instr(k.secret_bindings, '{"kind":"credential:user-state","key":"') + length('{"kind":"credential:user-state","key":"')), 1, instr(substr(k.secret_bindings, instr(k.secret_bindings, '{"kind":"credential:user-state","key":"') + length('{"kind":"credential:user-state","key":"')), '"') - 1)
    WHERE (length(k.secret_bindings) - length(replace(k.secret_bindings, '"credential:user-state"', ''))) / length('"credential:user-state"') = 1
      AND instr(k.secret_bindings, '{"kind":"credential:user-state","key":"' || u.id || '","ownerId":"' || u.auth_stamp || '"}') > 0)
  AND previous_secret_hash IS NOT NULL AND previous_secret_expires_at IS NOT NULL
  AND (NOT instr(previous_secret_bindings, '"credential:user-state"') > 0 OR id IN (SELECT k.id FROM api_keys k JOIN users u ON u.instance_id = k.instance_id AND u.id = substr(substr(k.previous_secret_bindings, instr(k.previous_secret_bindings, '{"kind":"credential:user-state","key":"') + length('{"kind":"credential:user-state","key":"')), 1, instr(substr(k.previous_secret_bindings, instr(k.previous_secret_bindings, '{"kind":"credential:user-state","key":"') + length('{"kind":"credential:user-state","key":"')), '"') - 1)
    WHERE (length(k.previous_secret_bindings) - length(replace(k.previous_secret_bindings, '"credential:user-state"', ''))) / length('"credential:user-state"') = 1
      AND instr(k.previous_secret_bindings, '{"kind":"credential:user-state","key":"' || u.id || '","ownerId":"' || u.auth_stamp || '"}') > 0));

UPDATE api_keys SET secret_hash = 'retired:' || id, secret_bindings = '[]', previous_secret_hash = NULL, previous_secret_expires_at = NULL,
  previous_secret_bindings = '[]', version = version || '.k2'
WHERE instr(secret_bindings, '"credential:user-state"') > 0
  AND id NOT IN (SELECT k.id FROM api_keys k JOIN users u ON u.instance_id = k.instance_id AND u.id = substr(substr(k.secret_bindings, instr(k.secret_bindings, '{"kind":"credential:user-state","key":"') + length('{"kind":"credential:user-state","key":"')), 1, instr(substr(k.secret_bindings, instr(k.secret_bindings, '{"kind":"credential:user-state","key":"') + length('{"kind":"credential:user-state","key":"')), '"') - 1)
    WHERE (length(k.secret_bindings) - length(replace(k.secret_bindings, '"credential:user-state"', ''))) / length('"credential:user-state"') = 1
      AND instr(k.secret_bindings, '{"kind":"credential:user-state","key":"' || u.id || '","ownerId":"' || u.auth_stamp || '"}') > 0);

UPDATE api_keys SET previous_secret_hash = NULL, previous_secret_expires_at = NULL, previous_secret_bindings = '[]', version = version || '.k2'
WHERE instr(previous_secret_bindings, '"credential:user-state"') > 0
  AND id NOT IN (SELECT k.id FROM api_keys k JOIN users u ON u.instance_id = k.instance_id AND u.id = substr(substr(k.previous_secret_bindings, instr(k.previous_secret_bindings, '{"kind":"credential:user-state","key":"') + length('{"kind":"credential:user-state","key":"')), 1, instr(substr(k.previous_secret_bindings, instr(k.previous_secret_bindings, '{"kind":"credential:user-state","key":"') + length('{"kind":"credential:user-state","key":"')), '"') - 1)
    WHERE (length(k.previous_secret_bindings) - length(replace(k.previous_secret_bindings, '"credential:user-state"', ''))) / length('"credential:user-state"') = 1
      AND instr(k.previous_secret_bindings, '{"kind":"credential:user-state","key":"' || u.id || '","ownerId":"' || u.auth_stamp || '"}') > 0);

UPDATE api_keys SET secret_bindings = replace(replace(replace(substr(secret_bindings, 1, instr(secret_bindings, '{"kind":"credential:user-state","key":"') - 1) || substr(secret_bindings, instr(secret_bindings, '{"kind":"credential:user-state","key":"') + instr(substr(secret_bindings, instr(secret_bindings, '{"kind":"credential:user-state","key":"')), '"}') + 1), '[,', '['), ',]', ']'), ',,', ','), version = version || '.k2'
WHERE instr(secret_bindings, '"credential:user-state"') > 0;

UPDATE api_keys SET previous_secret_bindings = replace(replace(replace(substr(previous_secret_bindings, 1, instr(previous_secret_bindings, '{"kind":"credential:user-state","key":"') - 1) || substr(previous_secret_bindings, instr(previous_secret_bindings, '{"kind":"credential:user-state","key":"') + instr(substr(previous_secret_bindings, instr(previous_secret_bindings, '{"kind":"credential:user-state","key":"')), '"}') + 1), '[,', '['), ',]', ']'), ',,', ','), version = version || '.k2'
WHERE instr(previous_secret_bindings, '"credential:user-state"') > 0;
