# Rysenova Single Click Attendene

A lightweight **.NET background worker** that automates employee attendance on the Rysenova platform.  
Designed to remove daily manual effort by handling login, shift detection, and check-in with minimal interaction.



## ✨ Features

- 🔐 Secure login with token caching  
- 🧠 Auto-refreshes expired tokens  
- ⏱️ Retrieves current attendance shift  
- ✅ Performs automated check-in  
- 🖥️ Console-based credential management  
- 📜 Clear success & failure logging



## 🛠️ Tech Stack

- .NET 8  
- C# BackgroundService  
- HttpClientFactory  
- System.Text.Json  
- Token-based authentication  



## 📂 Project Structure

KravMagaWorker/
├── Worker.cs
├── Program.cs
├── appsettings.json
├── README.md
