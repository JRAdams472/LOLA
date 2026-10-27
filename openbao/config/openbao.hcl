# OpenBao dev configuration — raft (integrated) storage so data survives
# `compose down`. OpenBao v2 removed the legacy `file` backend; raft is
# the supported persistent store.
# tls_disable is a DEV carve-out (plaintext inside the docker network,
# same convention as siblings). Real deployments terminate TLS and use
# Shamir-sealed storage — see docs/openbao-runbook.md.
storage "raft" {
  path    = "/openbao/file"
  node_id = "lola-dev-0"
}

listener "tcp" {
  address     = "0.0.0.0:8200"
  tls_disable = 1
}

api_addr     = "http://openbao:8200"
cluster_addr = "https://openbao:8201"

ui = false
disable_mlock = true
