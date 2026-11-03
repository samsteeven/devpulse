# syntax=docker/dockerfile:1.7
# Image d'un service .NET. Une seule recette pour tous, le projet est passé en argument :
#   docker build -f infrastructure/docker/dotnet.Dockerfile --build-arg PROJECT=src/Services/Identity/DevPulse.Identity.Api .

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
WORKDIR /src
ARG PROJECT

# Fichiers de projet d'abord : la restauration reste en cache tant qu'aucune dépendance ne change.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
COPY infrastructure/demo/ infrastructure/demo/
RUN --mount=type=cache,target=/root/.nuget/packages dotnet restore "$PROJECT"
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish "$PROJECT" -c Release -o /app --no-restore \
    && mv "/app/$(basename "$PROJECT")" /app/service

# Sans shell ni gestionnaire de paquets, utilisateur non root (UID 1654) : peu de surface d'attaque.
# « extra » ajoute ICU et les fuseaux horaires, que les formats de date et la normalisation des adresses utilisent.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0
EXPOSE 8080
ENTRYPOINT ["/app/service"]
