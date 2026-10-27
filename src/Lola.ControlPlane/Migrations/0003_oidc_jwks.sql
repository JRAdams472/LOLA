-- OpenIddict applications: JSON Web Key Set column for private_key_jwt
-- client auth (IOpenIddictApplicationStore.Get/SetJsonWebKeySetAsync).
ALTER TABLE openiddict_applications ADD COLUMN json_web_key_set TEXT;
