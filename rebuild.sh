#!/bin/bash
cd ~/jiratools-api
git pull
cd src/ApiJiraTools

# Cargar secrets desde archivo local (no va al repo)
source ~/jiratools-api/.env

sudo docker build --no-cache -t jiratools-api .
sudo docker rm -f jiratools-api
sudo docker run -d -p 8080:8080 \
  -e Jira__BaseUrl=https://easy-cash.atlassian.net \
  -e Jira__Email=ariel.garcia@finket.com.ar \
  -e Jira__ApiToken=$JIRA_API_TOKEN \
  -e Telegram__BotToken=$TELEGRAM_BOT_TOKEN \
  -e Telegram__AlertChatIds=8727833714 \
  -e Telegram__AlertProjects=CTA \
  -e Telegram__AlertHourUtc=12 \
  -e Telegram__AlertMinuteUtc=0 \
  -e "Telegram__DiscoveryProjectMapping=CTA:PC;NAT:IDEA" \
  -e Gemini__ApiKey=$GEMINI_API_KEY \
  -e Gemini__Model=gemini-2.5-flash \
  --name jiratools-api \
  --restart unless-stopped \
  jiratools-api
echo "Done. Checking health..."
sleep 3
curl -s http://localhost:8080/api/health
echo ""
