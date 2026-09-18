# Zebra's matching-architecture Debian CoreScanner + development .deb packages are
# supplied through the zebra-sdk build context, never downloaded implicitly.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS managed
WORKDIR /src
COPY agents/ ./agents/
RUN dotnet publish agents/linux/inventoryzing-scanner-agent/src/Inventoryzing.Agent.Scanner.Linux/Inventoryzing.Agent.Scanner.Linux.csproj -c Release -o /out -m:1

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble AS vendor
COPY --from=zebra-sdk *.deb /tmp/zebra/
# Prevent package post-install scripts from starting services during the build.
RUN printf '#!/bin/sh\nexit 101\n' > /usr/sbin/policy-rc.d && chmod +x /usr/sbin/policy-rc.d \
    && apt-get update && apt-get install -y --no-install-recommends /tmp/zebra/*.deb procps \
    && ldconfig && test -x /etc/init.d/cscored \
    && rm -rf /tmp/zebra /var/lib/apt/lists/* /usr/sbin/policy-rc.d

FROM vendor AS native
RUN apt-get update && apt-get install -y --no-install-recommends cmake g++ make
COPY agents/linux/inventoryzing-scanner-agent/native/corescanner-bridge /src
RUN cmake -S /src -B /build && cmake --build /build --parallel && cmake --install /build

FROM vendor
WORKDIR /app
COPY --from=managed /out/ ./
COPY --from=native /usr/local/bin/inventoryzing-zebra-bridge /usr/local/bin/
COPY deploy/docker/scanner-entrypoint.sh /usr/local/bin/scanner-entrypoint
RUN sed -i 's/\r$//' /usr/local/bin/scanner-entrypoint && chmod +x /usr/local/bin/scanner-entrypoint
ENTRYPOINT ["/usr/local/bin/scanner-entrypoint"]
