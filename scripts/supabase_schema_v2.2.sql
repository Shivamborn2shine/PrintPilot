-- ====================================================================
-- PrintAway SaaS Control Plane Schema (v2.2)
-- Daily Quota: 10 Pages/Day Free | Pro: ₹149/mo (Annual) or ₹249/mo
-- ====================================================================

-- 1. Franchise Directory & Live Fleet State
CREATE TABLE IF NOT EXISTS public.shops (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    shop_id TEXT UNIQUE NOT NULL, -- e.g. 'pilot', 'campus-01'
    shop_name TEXT NOT NULL,
    owner_name TEXT NOT NULL,
    owner_phone TEXT NOT NULL,
    owner_email TEXT,
    upi_vpa TEXT NOT NULL, -- Direct customer payment destination (e.g. shop@okhdfcbank)
    
    -- Subscription & Daily Quota
    subscription_tier TEXT NOT NULL DEFAULT 'free', -- 'free', 'pro_monthly', 'pro_annual'
    subscription_status TEXT NOT NULL DEFAULT 'active', -- 'active', 'past_due', 'suspended'
    daily_page_quota INT NOT NULL DEFAULT 10, -- 10 pages/day free
    valid_until TIMESTAMPTZ, -- Expiration for Pro tier
    
    -- Live Station Telemetry
    last_heartbeat_utc TIMESTAMPTZ,
    live_tunnel_url TEXT,
    active_printer_model TEXT,
    client_version TEXT DEFAULT '2.2.0',
    emergency_remote_pause BOOLEAN NOT NULL DEFAULT FALSE,
    
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

-- 2. Daily Telemetry (Zero PII - Daily Page Counting & Reset)
CREATE TABLE IF NOT EXISTS public.daily_telemetry (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    shop_id TEXT REFERENCES public.shops(shop_id) ON DELETE CASCADE,
    date DATE NOT NULL DEFAULT CURRENT_DATE,
    pages_count INT NOT NULL DEFAULT 0,
    sheets_count INT NOT NULL DEFAULT 0,
    orders_count INT NOT NULL DEFAULT 0,
    bw_pages INT NOT NULL DEFAULT 0,
    color_pages INT NOT NULL DEFAULT 0,
    revenue_inr NUMERIC(10,2) NOT NULL DEFAULT 0.00,
    last_updated TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE(shop_id, date)
);

-- 3. SaaS Subscription Payments (Shopkeeper paying Founder)
CREATE TABLE IF NOT EXISTS public.saas_subscriptions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    shop_id TEXT REFERENCES public.shops(shop_id) ON DELETE CASCADE,
    tier TEXT NOT NULL, -- 'pro_monthly' (₹249), 'pro_annual' (₹1,788)
    amount_inr NUMERIC(10,2) NOT NULL,
    payment_reference TEXT NOT NULL, -- UTR or Razorpay ID
    status TEXT NOT NULL DEFAULT 'active',
    valid_from TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    valid_until TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- 4. Fast Lookups & Row-Level Security
CREATE INDEX IF NOT EXISTS idx_shops_shop_id ON public.shops(shop_id);
CREATE INDEX IF NOT EXISTS idx_daily_telemetry_shop_date ON public.daily_telemetry(shop_id, date);

-- Enable RLS and grant read/write access to publishable API key for live browser dashboards and station sync
ALTER TABLE public.shops ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS "Allow public read to shops" ON public.shops;
CREATE POLICY "Allow public read to shops" ON public.shops FOR SELECT USING (true);
DROP POLICY IF EXISTS "Allow public insert to shops" ON public.shops;
CREATE POLICY "Allow public insert to shops" ON public.shops FOR INSERT WITH CHECK (true);
DROP POLICY IF EXISTS "Allow public update to shops" ON public.shops;
CREATE POLICY "Allow public update to shops" ON public.shops FOR UPDATE USING (true) WITH CHECK (true);

ALTER TABLE public.daily_telemetry ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS "Allow public read to daily_telemetry" ON public.daily_telemetry;
CREATE POLICY "Allow public read to daily_telemetry" ON public.daily_telemetry FOR SELECT USING (true);
DROP POLICY IF EXISTS "Allow public insert to daily_telemetry" ON public.daily_telemetry;
CREATE POLICY "Allow public insert to daily_telemetry" ON public.daily_telemetry FOR INSERT WITH CHECK (true);
DROP POLICY IF EXISTS "Allow public update to daily_telemetry" ON public.daily_telemetry;
CREATE POLICY "Allow public update to daily_telemetry" ON public.daily_telemetry FOR UPDATE USING (true) WITH CHECK (true);

ALTER TABLE public.saas_subscriptions ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS "Allow public read to saas_subscriptions" ON public.saas_subscriptions;
CREATE POLICY "Allow public read to saas_subscriptions" ON public.saas_subscriptions FOR SELECT USING (true);
DROP POLICY IF EXISTS "Allow public insert to saas_subscriptions" ON public.saas_subscriptions;
CREATE POLICY "Allow public insert to saas_subscriptions" ON public.saas_subscriptions FOR INSERT WITH CHECK (true);

-- 5. Seed Initial Pilot Shop
INSERT INTO public.shops (shop_id, shop_name, owner_name, owner_phone, upi_vpa, subscription_tier, daily_page_quota)
VALUES ('pilot', 'PrintAway Pilot Shop', 'Shivam', '9999999999', 'shivam@upi', 'pro_annual', 99999)
ON CONFLICT (shop_id) DO NOTHING;

