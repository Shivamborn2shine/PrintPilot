-- ====================================================================
-- PrintAway RLS Policy Fix (Run in Supabase SQL Editor)
-- Allows Franchise Registration & Live Telemetry Sync with Publishable Key
-- ====================================================================

-- 1. shops table permissions
ALTER TABLE public.shops ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS "Allow public read to shops" ON public.shops;
CREATE POLICY "Allow public read to shops" ON public.shops FOR SELECT USING (true);

DROP POLICY IF EXISTS "Allow public insert to shops" ON public.shops;
CREATE POLICY "Allow public insert to shops" ON public.shops FOR INSERT WITH CHECK (true);

DROP POLICY IF EXISTS "Allow public update to shops" ON public.shops;
CREATE POLICY "Allow public update to shops" ON public.shops FOR UPDATE USING (true) WITH CHECK (true);

-- 2. daily_telemetry table permissions
ALTER TABLE public.daily_telemetry ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS "Allow public read to daily_telemetry" ON public.daily_telemetry;
CREATE POLICY "Allow public read to daily_telemetry" ON public.daily_telemetry FOR SELECT USING (true);

DROP POLICY IF EXISTS "Allow public insert to daily_telemetry" ON public.daily_telemetry;
CREATE POLICY "Allow public insert to daily_telemetry" ON public.daily_telemetry FOR INSERT WITH CHECK (true);

DROP POLICY IF EXISTS "Allow public update to daily_telemetry" ON public.daily_telemetry;
CREATE POLICY "Allow public update to daily_telemetry" ON public.daily_telemetry FOR UPDATE USING (true) WITH CHECK (true);

-- 3. saas_subscriptions table permissions
ALTER TABLE public.saas_subscriptions ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS "Allow public read to saas_subscriptions" ON public.saas_subscriptions;
CREATE POLICY "Allow public read to saas_subscriptions" ON public.saas_subscriptions FOR SELECT USING (true);

DROP POLICY IF EXISTS "Allow public insert to saas_subscriptions" ON public.saas_subscriptions;
CREATE POLICY "Allow public insert to saas_subscriptions" ON public.saas_subscriptions FOR INSERT WITH CHECK (true);
