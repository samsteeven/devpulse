# syntax=docker/dockerfile:1.7
FROM node:24.22.2-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN --mount=type=cache,target=/root/.npm,sharing=locked npm ci
COPY . .
RUN npm run build

# nginx sans privilège (port 8080, utilisateur nginx) : sert l'application et relaie /api et /hubs vers la passerelle.
FROM nginxinc/nginx-unprivileged:1.30.6-alpine
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist /usr/share/nginx/html
ENV GATEWAY_URL=http://gateway:8080
EXPOSE 8080
