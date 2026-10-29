# The SPA, built with Vite and served by nginx, which also proxies /api to the API container
# (see nginx.conf). Built by ../compose.yaml; on its own: docker build -t kbms-web .

FROM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
# Empty = same origin: the browser calls /api on the nginx below. Set it to call an API elsewhere.
ARG VITE_API_BASE_URL=
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL
RUN npm run build

FROM nginx:1.30-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
