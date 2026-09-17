ALTER TABLE iz.roles
    ADD COLUMN version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    ADD COLUMN description text NOT NULL DEFAULT '';

INSERT INTO iz.permissions(code) VALUES
    ('role.manage'),
    ('role.assign'),
    ('user.manage')
ON CONFLICT DO NOTHING;

INSERT INTO iz.role_permissions(role_id, permission)
SELECT id, permission
FROM iz.roles
CROSS JOIN (VALUES ('role.manage'), ('role.assign'), ('user.manage')) AS added(permission)
WHERE iz.roles.name = 'Administrator'
ON CONFLICT DO NOTHING;
