# Yahtzee Meeting Platform

**A modern WPF-based desktop conference application**  
*Zoom-like video and audio meeting platform built with C# and WPF*

---

## 📋 Project Description

**Yahtzee Meeting Platform** is a client-server desktop application designed for organizing online meetings, video conferences, webinars, and team calls — a full-featured analog of Zoom.

The application consists of two separate WPF projects:
- **Client** — participant application (join meetings, video/audio, chat)
- **Server** — host/management application (creates and manages conferences, handles database and connections)

Both parts are built using **Windows Presentation Foundation (WPF)**, making them native Windows desktop applications with rich UI.

---

## ✨ Key Features

### Client Application
- User registration and login
- Guest mode
- Creating conferences 
- Join conference by id
- Conference planning
- Main conference window with real-time audio and video support
- Screen sharing
- Conference chat (group or personal)
- Files sharing in chat
- Intuitive modern WPF interface

### Server Application
- Built-in database management (`DatabaseService.cs`)
- Conference hosting and management
- Server builder logic (`ServerBuilder.cs`)
- Entity Framework integration for data persistence

### Common Capabilities
- Real-time communication via Berkeley Sockets
- Cross-module architecture (Client ↔ Server)
- Can work both locally and over the network

---

## 🛠 Technology Stack

- **Language**: C# 
- **UI Framework**: WPF (Windows Presentation Foundation)
- **Database**: Entity Framework (EF) + SQL Server / LocalDB (configured via `efpt.config.json`)
- **Architecture**: Client-Server (both desktop WPF applications)
- **Solution files**: `Client.sln` and `Server.sln`
- **Project files**: `Client.csproj` and `Server.csproj`

**Target platform**: Windows (native WPF desktop apps)

---

## 🚀 How to Build and Run

### Prerequisites
- Visual Studio 2022 or newer (with **.NET Desktop Development** workload)
- Windows 10/11
- .NET Framework / .NET SDK (depending on TargetFramework in .csproj)

### Step-by-step

1. **Clone the repository**
   ```bash
   git clone https://github.com/Bereh9977/Yahtzee-meeting-platfotm.git
   cd Yahtzee-meeting-platfotm

2. **Open solutions in Visual Studio**
3. **Install EF Core Core Power Tools extention**
4. **Open Server/Server.sln**
5. **Update server name according to your SQL Server / LocalDB setup.**
   Database connection is configured inside Server/Models/MeetingsContext.cs. 
7. **Open Terminal or Package Manager Console and run:**
   ```bash
   Add-Migration Init
   Update-Database
8. **Update server port if needed in MainWindow.xaml.cs in StartServerAsync** 
9. **Open Client/Client.sln**
10. **Update IP address and port if needed in LoginWindow.xaml.cs and MainWindow.xaml.cs.**
    You can do this by hitting Ctrl+F and replacing old values with new in current project. 
12. **Open Server/Server.sln → set as Startup Project → Run (F5)**
13. **Open Client/Client.sln → set as Startup Project → Run (F5)**

### Run order
- First start the Server application
- Then start the Client application(s)
- Log in and create/join a conference 

Note: Server **must** be running for clients to connect.

---

## Contributing
Contributions are welcome!
If you want to add new features or fix bugs:
- Fork the repository
- Create a feature/bug branch (git checkout -b feature/some-feature or git checkout -b bug/some-bug)
- Commit your changes
- Open a Pull Request

---

## Authors
Nazar Berehchuk (GitHub: @Bereh9977)
Anastasiia Kolomiiets (GitHub: @anastasiia-kolomiiets)
Artem Pryimachenko (GitHub: @shadexcess)

---

Built with ❤️ using WPF for native Windows conferencing experience
Last project update: May 2025
