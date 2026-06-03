# Ubuntu 24 Deployment

These notes assume the app will run from `/opt/shortlinks`, nginx is installed directly on the Ubuntu 24 server, Certbot manages TLS certificates, and the public URL is `https://go.furman87.com`.

## 1. DNS

Create a DNS record for the subdomain:

```text
Type: A
Name: go
Value: your-server-public-ip
```

If you use IPv6, also add an `AAAA` record for `go`.

Wait until DNS resolves before requesting a certificate:

```bash
dig go.furman87.com
```

## 2. Server Packages

Install Docker, the Compose plugin, nginx, and Certbot:

```bash
sudo apt update
sudo apt install -y ca-certificates curl gnupg nginx certbot python3-certbot-nginx
```

Install Docker from Docker's Ubuntu repository:

```bash
sudo install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg | sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
sudo chmod a+r /etc/apt/keyrings/docker.gpg
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu noble stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt update
sudo apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
```

Optional, but convenient: allow your user to run Docker without `sudo`.

```bash
sudo usermod -aG docker "$USER"
```

Log out and back in after changing group membership.

## 3. App Folder

Create the production folder:

```bash
sudo mkdir -p /opt/shortlinks
sudo chown -R "$USER":"$USER" /opt/shortlinks
```

Get the project files into `/opt/shortlinks`.

Option A: clone from GitHub on the server:

```bash
cd /opt
git clone https://github.com/your-github-user/your-repo-name.git shortlinks
cd /opt/shortlinks
```

If the repository is private, use an SSH clone URL instead:

```bash
cd /opt
git clone git@github.com:your-github-user/your-repo-name.git shortlinks
cd /opt/shortlinks
```

Option B: copy from your development machine with `scp`:

```bash
scp -r ./BitlyClone/* your-user@your-server:/opt/shortlinks/
```

On the server, confirm the important files are present:

```bash
cd /opt/shortlinks
ls
```

You should see `docker-compose.yml`, `ShortLinks/`, `.env.example`, and this deployment file.

## 4. Environment File

Create the real `.env` file from the example:

```bash
cd /opt/shortlinks
cp .env.example .env
chmod 600 .env
```

Generate a strong Postgres password:

```bash
openssl rand -base64 48
```

Edit `.env`:

```bash
nano .env
```

Set values like this:

```text
POSTGRES_PASSWORD=paste-your-generated-password-here
PUBLIC_BASE_URL=https://go.furman87.com
```

Important: choose the password before the first `docker compose up`. Postgres uses `POSTGRES_PASSWORD` when the database volume is initialized. If you change it later, the existing database user's password inside the persisted volume is not automatically changed.

## 5. Start Docker Compose

Build and start the app:

```bash
cd /opt/shortlinks
docker compose up -d --build
```

Check status:

```bash
docker compose ps
```

Check logs:

```bash
docker compose logs -f app
docker compose logs -f db
```

The compose file binds the app to `127.0.0.1:8080`, so nginx can reach it locally but the app port is not exposed directly to the internet.

Test locally on the server:

```bash
curl -I http://127.0.0.1:8080
```

## 6. Nginx Reverse Proxy

Create an nginx site file:

```bash
sudo nano /etc/nginx/sites-available/shortlinks
```

Use this config:

```nginx
server {
    listen 80;
    listen [::]:80;
    server_name go.furman87.com;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

Enable the site:

```bash
sudo ln -s /etc/nginx/sites-available/shortlinks /etc/nginx/sites-enabled/shortlinks
sudo nginx -t
sudo systemctl reload nginx
```

If the default nginx site conflicts, disable it:

```bash
sudo rm /etc/nginx/sites-enabled/default
sudo nginx -t
sudo systemctl reload nginx
```

## 7. Certbot TLS Certificate

Request and install the certificate:

```bash
sudo certbot --nginx -d go.furman87.com
```

Choose the redirect-to-HTTPS option when prompted.

Test renewal:

```bash
sudo certbot renew --dry-run
```

Certbot usually installs a systemd timer automatically. Confirm it exists:

```bash
systemctl list-timers | grep certbot
```

## 8. Firewall

If you use UFW, allow SSH and nginx:

```bash
sudo ufw allow OpenSSH
sudo ufw allow 'Nginx Full'
sudo ufw enable
sudo ufw status
```

You do not need to open port `8080`; Docker binds it only to `127.0.0.1`.

## 9. Updating Later

Copy or pull the updated code into `/opt/shortlinks`, then rebuild and recreate the app container:

```bash
cd /opt/shortlinks
docker compose up -d --build app
```

If you changed `docker-compose.yml`, also run:

```bash
docker compose up -d --build
```

Check the result:

```bash
docker compose ps
docker compose logs --tail 100 app
curl -I https://go.furman87.com
```

Clean old Docker build cache occasionally:

```bash
docker system prune
```

Do not use `docker compose down -v` in production unless you intentionally want to delete the Postgres data volume.

## 10. Backup Notes

The database lives in the Docker volume named `shortlinks_postgres-data` or a similar Compose-generated name. Check the exact name:

```bash
docker volume ls | grep postgres
```

Create a SQL backup:

```bash
cd /opt/shortlinks
docker compose exec -T db pg_dump -U shortlinks shortlinks > shortlinks-backup.sql
```

Restore a backup into an existing database only when you are sure you want to overwrite or merge data. Take the app offline first:

```bash
docker compose stop app
docker compose exec -T db psql -U shortlinks shortlinks < shortlinks-backup.sql
docker compose start app
```
