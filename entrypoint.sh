#!/bin/sh
echo "Generate Rclon config"
sh /generate-config.sh
echo "creating crontab"
# echo -e "$CRON_SCHEDULE /backup.sh\n" > /etc/crontabs/root
echo "starting crond"
#crond -f
