ALTER TABLE iz.sites
    ADD COLUMN settings_version bigint NOT NULL DEFAULT 1 CHECK (settings_version > 0),
    ADD COLUMN logo_png bytea,
    ADD COLUMN logo_updated_at timestamptz;

INSERT INTO iz.permissions(code) VALUES
    ('system.config.view'),
    ('system.config.edit')
ON CONFLICT DO NOTHING;

INSERT INTO iz.role_permissions(role_id, permission)
SELECT id, permission
FROM iz.roles
CROSS JOIN (VALUES ('system.config.view'), ('system.config.edit')) AS added(permission)
WHERE iz.roles.name = 'Administrator'
ON CONFLICT DO NOTHING;
