export const discordPostCommandDocumentType = "bifrost.bridge.discord_post_command";
export const discordPostCommandSchemaId = "bifrost.bridge.discord_post_command.v1";
export const discordPostReceiptDocumentType = "bifrost.bridge.discord_post_receipt";
export const discordPostReceiptSchemaId = "bifrost.bridge.discord_post_receipt.v1";

let cachedDefinitions;

export function discordPostCommandDefinition(defineDocumentType) {
  cachedDefinitions ??= {};
  cachedDefinitions.command ??= defineDocumentType({
    type: discordPostCommandDocumentType,
    schemaName: discordPostCommandDocumentType,
    schemaId: discordPostCommandSchemaId,
    schemaVersion: discordPostCommandSchemaId,
    contentHash: discordPostCommandSchemaId,
    global: false,
    name: "commandId",
    schema: objectSchema("Bifrost Discord post command"),
  });
  return cachedDefinitions.command;
}

export function discordPostReceiptDefinition(defineDocumentType) {
  cachedDefinitions ??= {};
  cachedDefinitions.receipt ??= defineDocumentType({
    type: discordPostReceiptDocumentType,
    schemaName: discordPostReceiptDocumentType,
    schemaId: discordPostReceiptSchemaId,
    schemaVersion: discordPostReceiptSchemaId,
    contentHash: discordPostReceiptSchemaId,
    global: false,
    name: "commandId",
    schema: objectSchema("Bifrost Discord post receipt"),
  });
  return cachedDefinitions.receipt;
}

function objectSchema(label) {
  return {
    parse(input) {
      if (!input || typeof input !== "object" || Array.isArray(input)) {
        throw new Error(`${label} must be an object.`);
      }
      return input;
    },
  };
}
