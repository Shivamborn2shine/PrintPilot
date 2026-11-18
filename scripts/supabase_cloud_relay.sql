-- ============================================================================
-- PrintAway Cloud Relay Fallback Schema (ScanDrop Hybrid Architecture)
-- Run once in Supabase SQL Editor (or via Supabase MCP) to activate Tier 2
-- zero-fail uploads whenever a shopkeeper tunnel returns 502 or is blocked.
-- ============================================================================

CREATE TABLE IF NOT EXISTS public.relay_orders (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    relay_id TEXT UNIQUE NOT NULL,
    shop_id TEXT NOT NULL,
    file_name TEXT NOT NULL,
    storage_path TEXT NOT NULL,
    file_sha256 TEXT,
    file_size_bytes BIGINT NOT NULL DEFAULT 0,
    copies INTEGER NOT NULL DEFAULT 1,
    duplex_mode TEXT NOT NULL DEFAULT 'OneSided',
    color_mode TEXT NOT NULL DEFAULT 'bw',
    paper_size TEXT NOT NULL DEFAULT 'A4',
    pages_per_sheet INTEGER NOT NULL DEFAULT 1,
    page_range TEXT NOT NULL DEFAULT 'all',
    customer_name TEXT,
    customer_phone TEXT,
    payment_method TEXT NOT NULL DEFAULT 'cash',
    status TEXT NOT NULL DEFAULT 'pending', -- pending | processing | accepted | failed
    order_id BIGINT,
    order_code TEXT,
    public_token TEXT,
    amount_inr NUMERIC(10, 2),
    amount_paise BIGINT,
    payment_status TEXT,
    order_state TEXT,
    gateway_order_id TEXT,
    razorpay_order_id TEXT,
    razorpay_key_id TEXT,
    error_message TEXT,
    processed_at_utc TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_relay_orders_shop_status
    ON public.relay_orders (shop_id, status, created_at);

GRANT SELECT, INSERT, UPDATE ON public.relay_orders TO anon, authenticated;

ALTER TABLE public.relay_orders ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS "relay_orders_select" ON public.relay_orders;
CREATE POLICY "relay_orders_select"
    ON public.relay_orders
    FOR SELECT
    TO anon, authenticated
    USING (true);

DROP POLICY IF EXISTS "relay_orders_insert" ON public.relay_orders;
CREATE POLICY "relay_orders_insert"
    ON public.relay_orders
    FOR INSERT
    TO anon, authenticated
    WITH CHECK (true);

DROP POLICY IF EXISTS "relay_orders_update" ON public.relay_orders;
CREATE POLICY "relay_orders_update"
    ON public.relay_orders
    FOR UPDATE
    TO anon, authenticated
    USING (true)
    WITH CHECK (true);

-- Storage Bucket for transient 50 MB PDF transfers (deleted automatically on PC pickup)
INSERT INTO storage.buckets (id, name, public, file_size_limit)
VALUES ('printaway-relay', 'printaway-relay', false, 52428800)
ON CONFLICT (id) DO NOTHING;

DROP POLICY IF EXISTS "printaway_relay_insert" ON storage.objects;
CREATE POLICY "printaway_relay_insert"
    ON storage.objects
    FOR INSERT
    TO anon, authenticated
    WITH CHECK (bucket_id = 'printaway-relay');

DROP POLICY IF EXISTS "printaway_relay_select" ON storage.objects;
CREATE POLICY "printaway_relay_select"
    ON storage.objects
    FOR SELECT
    TO anon, authenticated
    USING (bucket_id = 'printaway-relay');

DROP POLICY IF EXISTS "printaway_relay_delete" ON storage.objects;
CREATE POLICY "printaway_relay_delete"
    ON storage.objects
    FOR DELETE
    TO anon, authenticated
    USING (bucket_id = 'printaway-relay');
