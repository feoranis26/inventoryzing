ALTER TABLE iz_print.label_templates
    ADD COLUMN draft_width_mm numeric(8,3),
    ADD COLUMN draft_height_mm numeric(8,3),
    ADD COLUMN draft_media_kind text,
    ADD COLUMN draft_definition jsonb,
    ADD COLUMN draft_updated_by uuid REFERENCES iz.accounts;

UPDATE iz_print.label_templates template
SET draft_width_mm = revision.width_mm,
    draft_height_mm = revision.height_mm,
    draft_media_kind = revision.media_kind,
    draft_definition = revision.definition,
    draft_updated_by = revision.created_by
FROM iz_print.label_template_revisions revision
WHERE revision.template_id = template.id
  AND revision.revision = template.current_revision;

ALTER TABLE iz_print.label_templates
    ALTER COLUMN draft_width_mm SET NOT NULL,
    ALTER COLUMN draft_height_mm SET NOT NULL,
    ALTER COLUMN draft_media_kind SET NOT NULL,
    ALTER COLUMN draft_definition SET NOT NULL,
    ALTER COLUMN draft_updated_by SET NOT NULL,
    ADD CONSTRAINT label_template_draft_width
        CHECK (draft_width_mm > 0 AND draft_width_mm <= 300),
    ADD CONSTRAINT label_template_draft_height
        CHECK (draft_height_mm > 0 AND draft_height_mm <= 1000),
    ADD CONSTRAINT label_template_draft_media_kind
        CHECK (draft_media_kind IN ('die_cut', 'continuous')),
    ADD CONSTRAINT label_template_draft_definition
        CHECK (jsonb_typeof(draft_definition) = 'object');
