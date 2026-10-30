FROM mono:6.12

RUN sed -i s/deb.debian.org/archive.debian.org/g /etc/apt/sources.list \
    && sed -i s/security.debian.org/archive.debian.org/g /etc/apt/sources.list \
    && apt-get -o Acquire::Check-Valid-Until=false update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends mono-xsp4 sqlite3 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY . /app
RUN mkdir -p /app/App_Data /app/bin && cp /usr/lib/mono/4.5/Mono.Data.Sqlite.dll /app/bin/ && chmod 777 /app/App_Data && chmod +x /app/scripts/*.sh
EXPOSE 8080
CMD ["xsp4", "--nonstop", "--port", "8080", "--address", "0.0.0.0", "--root", "/app"]
