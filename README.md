# IProgram

IProgram is a comprehensive employee management system with a modern web application built using Angular for the client-side and ASP.NET Core for the backend API.

## Project Structure

The project is organized into several main components:

- **Client/**: Angular frontend application
- **src/**: Backend API application following Clean Architecture principles

### Backend Architecture

The backend follows a Clean Architecture pattern with the following layers:

- **Core/**: Contains the domain models, interfaces, and core business logic
- **Infrastructure/**: Implements interfaces defined in Core and contains external service integrations
- **Application/**: Contains application-specific logic, DTOs, and features
- **Persistence/**: Handles data access, repositories, and database-related logic
- **Api/**: ASP.NET Core controllers and API-specific implementations

## Features

- Employee management (CRUD operations)
- Employee data import/export via Excel files
- User authentication and authorization
- Role-based access control
- Reporting capabilities
- College and section management

## Getting Started

### Prerequisites

- [.NET 7.0 SDK](https://dotnet.microsoft.com/download/dotnet/7.0) or later
- [Node.js 16+](https://nodejs.org/)
- [Angular CLI](https://angular.io/cli)

### Installation

1. Clone the repository:
   ```bash
   git clone [repository-url]
   cd IProgram
   ```

2. Install backend dependencies:
   ```bash
   cd src
   dotnet restore
   ```

3. Install frontend dependencies:
   ```bash
   cd ../Client
   npm install
   ```

### Running the Application

1. **Backend API**:
   ```bash
   cd ../src
   dotnet run
   ```
   The API will be available at `https://localhost:5001` (or `http://localhost:5000` in development).

2. **Frontend Application**:
   ```bash
   cd ../Client
   ng serve
   ```
   The Angular app will be available at `http://localhost:4200`.

## API Documentation

API documentation is available through Swagger at `https://localhost:5001/swagger` when running the API in development mode.

## Authentication

The application uses JWT (JSON Web Tokens) for authentication. After login, the token should be included in the Authorization header as a Bearer token for API requests.

## File Upload

The API supports file uploads up to 10MB for employee data in Excel format. Files can be uploaded through the `/api/employee/upload` endpoint.

## Development

### Adding New Features

1. Define models and interfaces in the Core layer
2. Implement repository logic in Persistence
3. Create application services in Application
4. Add API controllers in the Api layer
5. Implement UI components in the Client application

### Code Organization

- Follow Clean Architecture principles
- Implement proper separation of concerns
- Use dependency injection for service management
- Implement proper error handling and logging

## License

This project is licensed under the MIT License.
