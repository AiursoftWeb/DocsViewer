#!/bin/sh
set -e
mkdir -p /var/lib/aiursoft-docsViewer
chown www-data:www-data /var/lib/aiursoft-docsViewer

# Bootstrap config if missing (matching Docker entrypoint pattern)
if [ ! -f /etc/aiursoft-docsViewer/appsettings.json ]; then
    cp /usr/share/aiursoft-docsViewer/appsettings.json /etc/aiursoft-docsViewer/appsettings.json
fi
# Replace /usr/share copy with symlink to /etc
rm -f /usr/share/aiursoft-docsViewer/appsettings.json
ln -sf /etc/aiursoft-docsViewer/appsettings.json /usr/share/aiursoft-docsViewer/appsettings.json
