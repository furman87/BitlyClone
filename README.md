# ShortLinks

A small Bitly-style shortener for a personal server. My favorite short subdomain for this is `go.furman87.com`: it is short, memorable, and reads naturally when pasted into a message. Other good options are `ln.furman87.com`, `zip.furman87.com`, `hop.furman87.com`, and `s.furman87.com`.

## Run Locally

```powershell
$env:POSTGRES_PASSWORD="replace-with-a-long-random-password"
$env:PUBLIC_BASE_URL="https://go.furman87.com"
docker compose up --build
```

The app listens on `http://localhost:8080` by default. Put your reverse proxy in front of it and point `go.furman87.com` to the proxy.

## Deployment

See [DEPLOYMENT.md](DEPLOYMENT.md) for Ubuntu 24 instructions using `/opt/shortlinks`, nginx, Certbot, Docker Compose, and `go.furman87.com`.

## Security Notes

This app intentionally accepts only `http` and `https` URLs, rejects embedded credentials, strips URL fragments, rate-limits short-link creation, blocks `localhost`, blocks `furman87.com` and subdomains, and rejects hosts that are literal or DNS-resolved private/reserved network addresses. Those checks reduce common SSRF, phishing, and self-referential redirect abuse.

Recommended production hardening:

- Set a strong `POSTGRES_PASSWORD` in an `.env` file or server secret store.
- Put the container behind HTTPS with a reverse proxy such as Caddy, nginx, or Traefik.
- Keep the Postgres port private to the Docker network.
- Add an allowlist or admin login if you do not want the public to create links.
- Add abuse monitoring, deletion/admin tools, and optional malware/phishing checks before making the service broadly public.
