INSERT INTO iz.permissions(code) VALUES ('scanner.agent') ON CONFLICT DO NOTHING;

INSERT INTO iz.role_permissions(role_id, permission)
SELECT id, 'scanner.agent' FROM iz.roles WHERE name = 'Administrator'
ON CONFLICT DO NOTHING;
