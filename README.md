# Multi-Player Mobile Crystal Game

A real-time multiplayer mobile game project developed as an Advanced Software Engineering individual assignment. The game features location-based gameplay using GPS, UDP networking for low-latency multiplayer interactions, an energy system restricting player actions, and a host-authoritative architecture with anti-cheat mechanisms.

## 🎮 Features

- **Real-time multiplayer crystal collection** game supporting up to 4 players
- **Location-aware gameplay** integrating mobile GPS data for positioning
- **Host-authoritative peer-to-peer networking** using UDP sockets for low latency
- **Energy system** limiting player ability to collect crystals and engage in duels
- **Anti-cheat mechanisms** to ensure fair play and prevent GPS/location spoofing
- **ClientSim framework** for automated testing of multiplayer scenarios on single device
- **Cross-platform mobile compatibility** for Android and iOS devices

## 📋 Requirements

- Unity 2022.3 LTS or later
- Android/iOS device with GPS capabilities for multiplayer testing
- .NET 6.0 runtime environment
- Network connectivity for multiplayer gameplay

## 🚀 Getting Started

1. **Clone this repository**
   ```bash
   git clone [repository-url]
   cd crystal-hunt-game
   ```

2. **Open the Unity project**
   - Launch Unity Hub
   - Click "Open" and select the project folder
   - Ensure Unity 2022.3 LTS or later is installed

3. **Build and deploy**
   - For mobile testing: Build and deploy to your Android/iOS device
   - For local testing: Use the ClientSim framework in Unity Editor

4. **Start multiplayer session**
   - Connect devices to the same local network
   - One player hosts, others join the session
   - Begin crystal collection gameplay

## 📁 Project Structure

```
Assets/
├── Scripts/
│   ├── Networking/          # UDP networking components and message handling
│   ├── GameMechanics/       # Crystal collection, energy, and duel logic
│   ├── GPS/                 # Location services and GPS integration
│   ├── Security/            # Anti-cheat and input validation
│   └── ClientSim/           # Automated multiplayer testing framework
├── Scenes/                  # Main game scenes and UI
├── Prefabs/                 # Game objects and UI elements
└── Documentation/           # Architecture and design documents
```

## 🛠️ Development Practices

This project follows **eXtreme Programming (XP)** methodology:

- **Pair Programming**: All code developed through collaborative sessions
- **Test-Driven Development (TDD)**: Comprehensive unit and integration testing
- **Continuous Integration**: Automated testing using ClientSim framework
- **Simple Design**: Focus on essential features without over-engineering
- **Collective Code Ownership**: Shared responsibility for entire codebase
- **Sustainable Pace**: 10 hours/week development commitment
- **Consistent Coding Standards**: Unified C# conventions throughout

## 🎯 Gameplay Mechanics

### **Crystal Collection**
- Players explore real-world locations to find virtual crystals
- GPS coordinates determine crystal spawn locations
- Energy system limits collection frequency

### **Multiplayer Duels**
- Challenge nearby players to competitive encounters
- Host-authoritative validation prevents cheating
- Energy cost balances engagement frequency

### **Energy Management**
- Limited energy pool restricts player actions
- Strategic resource management required
- Regeneration mechanics encourage thoughtful gameplay

## ⚙️ Technical Architecture

### **Networking**
- **UDP-based communication** for real-time performance
- **Host-authoritative architecture** ensuring game state consistency
- **Message serialization** for efficient data transmission
- **Network synchronization** handling latency and packet loss

### **Security**
- **GPS spoofing detection** through multiple validation layers
- **Input sanitization** preventing malicious data injection
- **Server-side validation** of all critical game actions
- **Anti-cheat monitoring** during gameplay sessions

### **Testing**
- **ClientSim framework** enabling single-device multiplayer testing
- **Automated test suites** for networking and game logic
- **Integration testing** for complex multiplayer scenarios
- **Performance testing** for mobile optimization

## 🚧 Known Limitations

- **Firebase integration** is partially implemented and requires manual configuration
- **GPS accuracy** varies based on device hardware and environmental conditions
- **Security measures** focus on common attack vectors but may not cover all scenarios
- **Network optimization** prioritizes functionality over advanced performance tuning

## 📚 Documentation

- **Architecture Document**: Detailed system design and component interactions
- **Development Plan**: XP methodology implementation and project timeline
- **Use Cases**: Comprehensive gameplay scenarios and requirements
- **Weekly Diary**: Development progress tracking and lessons learned

## 👥 Contributors

- **Tadepalli Sai Subrahmanya Srikar** - Lead Developer
- **Chandana Kakkunuru** - Collaborative Development Partner

Developed following Advanced Software Engineering course requirements with emphasis on collaborative development practices and software engineering methodologies.
