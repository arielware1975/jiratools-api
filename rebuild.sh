#!/bin/bash
cd ~/jiratools-api
git pull
cd src/ApiJiraTools
sudo docker build -t jiratools-api .
sudo docker rm -f jiratools-api
sudo docker run -d -p 8080:8080 \
  -e Jira__BaseUrl=https://easy-cash.atlassian.net \
  -e Jira__Email=ariel.garcia@finket.com.ar \
  -e Jira__ApiToken=ATATT3xFfGF0eNhCiCp2NeOJeL_rKdmcGVsmxf-t2UBON7Z_p4eL93IQUP33vGX9eZmUega81KriC1HNQ62eTDsr9acM70sj5dxCqVOPCaT1INISEO0CszCkSMIU54WvW9uQ24X7g0O4aB3YiALBRMYWTpnZ-WMNGyAp9jzseDHFzIbLR6H62f8=F9943386 \
  -e Telegram__BotToken=8782084295:AAFodXpeRc4OGDPYNnChKAMa7O-UMQSgwm0 \
  -e Telegram__AlertChatIds=8727833714 \
  -e Telegram__AlertProjects=CTA \
  -e Telegram__AlertHourUtc=12 \
  -e Telegram__AlertMinuteUtc=0 \
  -e Telegram__DiscoveryProjectMapping=CTA:PC \
  --name jiratools-api \
  --restart unless-stopped \
  jiratools-api
echo "Done. Checking health..."
sleep 3
curl -s http://localhost:8080/api/health
echo ""
