INSERT INTO iz.permissions(code) VALUES ('label.template.manage')
ON CONFLICT DO NOTHING;

INSERT INTO iz.role_permissions(role_id, permission)
SELECT id, 'label.template.manage' FROM iz.roles WHERE name = 'Administrator'
ON CONFLICT DO NOTHING;

CREATE TABLE iz_print.label_templates (
    id uuid PRIMARY KEY,
    site_id uuid NOT NULL REFERENCES iz.sites,
    name text NOT NULL CHECK (length(trim(name)) > 0 AND length(name) <= 160),
    current_revision integer NOT NULL DEFAULT 1 CHECK (current_revision > 0),
    is_default boolean NOT NULL DEFAULT false,
    created_by uuid NOT NULL REFERENCES iz.accounts,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(site_id, name),
    UNIQUE(id, site_id)
);
CREATE UNIQUE INDEX one_default_label_template
    ON iz_print.label_templates(site_id) WHERE is_default;

CREATE TABLE iz_print.label_template_revisions (
    template_id uuid NOT NULL REFERENCES iz_print.label_templates,
    revision integer NOT NULL CHECK (revision > 0),
    width_mm numeric(8,3) NOT NULL CHECK (width_mm > 0 AND width_mm <= 300),
    height_mm numeric(8,3) NOT NULL CHECK (height_mm > 0 AND height_mm <= 1000),
    media_kind text NOT NULL CHECK (media_kind IN ('die_cut', 'continuous')),
    definition jsonb NOT NULL CHECK (jsonb_typeof(definition) = 'object'),
    created_by uuid NOT NULL REFERENCES iz.accounts,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(template_id, revision)
);
CREATE TRIGGER immutable_label_template_revisions
    BEFORE UPDATE OR DELETE ON iz_print.label_template_revisions
    FOR EACH ROW EXECUTE FUNCTION iz.prevent_change();

INSERT INTO iz_print.label_templates(id, site_id, name, is_default, created_by)
SELECT gen_random_uuid(), site.id, 'Standard object label', true, account.id
FROM iz.sites site
JOIN LATERAL (
    SELECT account.id FROM iz.accounts account
    WHERE account.account_kind = 'human' ORDER BY account.id LIMIT 1
) account ON true
WHERE site.local;

INSERT INTO iz_print.label_template_revisions(
    template_id, revision, width_mm, height_mm, media_kind, definition, created_by)
SELECT template.id, 1, 29, 90, 'die_cut',
    jsonb_build_object('elements', jsonb_build_array(
        jsonb_build_object('id', 'main-qr', 'kind', 'qr',
            'x_mm', 2, 'y_mm', 3, 'width_mm', 25, 'height_mm', 25,
            'content', '{object.uuid}', 'font_size_mm', 3, 'align', 'center'),
        jsonb_build_object('id', 'object-name', 'kind', 'text',
            'x_mm', 2, 'y_mm', 31, 'width_mm', 25, 'height_mm', 9,
            'content', '{object.name}', 'font_size_mm', 3, 'align', 'center'),
        jsonb_build_object('id', 'short-uuid', 'kind', 'field',
            'x_mm', 2, 'y_mm', 43, 'width_mm', 25, 'height_mm', 6,
            'content', '{object.uuid[-8:]}', 'font_size_mm', 2.5, 'align', 'center'),
        jsonb_build_object('id', 'site-brand', 'kind', 'site_branding',
            'x_mm', 2, 'y_mm', 74, 'width_mm', 25, 'height_mm', 5,
            'content', '', 'font_size_mm', 2.3, 'align', 'center'),
        jsonb_build_object('id', 'inventoryzing-brand', 'kind', 'inventoryzing_branding',
            'x_mm', 2, 'y_mm', 81, 'width_mm', 25, 'height_mm', 5,
            'content', '', 'font_size_mm', 2.3, 'align', 'center')
    )), template.created_by
FROM iz_print.label_templates template
WHERE template.name = 'Standard object label';
