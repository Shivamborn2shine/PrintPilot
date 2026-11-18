// Relay for Cloudflare quick-tunnel sign-up.
//
// Some shop networks block every *.trycloudflare.com hostname, including api.trycloudflare.com,
// which cloudflared calls once to get a quick tunnel. The tunnel itself runs over Cloudflare's
// edge (port 7844), which those networks still allow. The station therefore starts cloudflared
// with `--quick-service https://<storefront>/api/cf-quick`, and cloudflared POSTs to this route
// instead; we forward that single request unchanged and hand back Cloudflare's reply.
//
// Only this one fixed upstream call is possible: no URL, headers or body are taken from the caller.

const UPSTREAM = 'https://api.trycloudflare.com/tunnel';

module.exports = async (req, res) => {
  res.setHeader('Cache-Control', 'no-store');

  if (req.method !== 'POST') {
    res.statusCode = 405;
    res.setHeader('Allow', 'POST');
    res.setHeader('Content-Type', 'application/json');
    res.end(JSON.stringify({ success: false, errors: [{ message: 'POST only' }] }));
    return;
  }

  const ua = String(req.headers['user-agent'] || '');
  try {
    const upstream = await fetch(UPSTREAM, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'User-Agent': /^cloudflared\//i.test(ua) ? ua : 'cloudflared/printaway-relay',
      },
      signal: AbortSignal.timeout(15000),
    });
    const body = await upstream.text();
    res.statusCode = upstream.status;
    res.setHeader('Content-Type', upstream.headers.get('content-type') || 'application/json');
    res.end(body);
  } catch (err) {
    res.statusCode = 502;
    res.setHeader('Content-Type', 'application/json');
    res.end(JSON.stringify({ success: false, errors: [{ message: 'relay could not reach Cloudflare: ' + (err && err.message) }] }));
  }
};
