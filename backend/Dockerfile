# KnowledgeBase.Api image. Built by ../compose.yaml; on its own: docker build -t kbms-api .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Project files first, so the restore layer is reused until a package changes.
COPY src/KnowledgeBase.Core/KnowledgeBase.Core.csproj src/KnowledgeBase.Core/
COPY src/KnowledgeBase.Api/KnowledgeBase.Api.csproj src/KnowledgeBase.Api/
RUN dotnet restore src/KnowledgeBase.Api/KnowledgeBase.Api.csproj
COPY src/ src/
RUN dotnet publish src/KnowledgeBase.Api/KnowledgeBase.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# OCR for scanned PDFs and photos (Ocr section): Tesseract with English, Filipino and page-orientation
# data, and pdftoppm to render PDF pages. Another language: add tesseract-ocr-<code> here and the
# code to Ocr:Languages.
RUN apt-get update \
 && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-eng tesseract-ocr-fil tesseract-ocr-osd poppler-utils \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
# The image's non-root user; listens on 8080 (ASPNETCORE_HTTP_PORTS).
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "KnowledgeBase.Api.dll"]
