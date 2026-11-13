// The Linked accounts page's passkey ceremonies (docs/passkeys.md). First-party, no dependency. It asks the server for
// WebAuthn options, calls navigator.credentials, and posts back only base64url strings in a same-origin form. Nothing it
// reads from the options is shown or evaluated; the server checks the origin, the challenge and the signature.
(function () {
  "use strict";

  var status = document.getElementById("passkey-status");

  function say(text) {
    if (status) {
      status.textContent = text;
    }
  }

  function toBytes(value) {
    var padded = value.replace(/-/g, "+").replace(/_/g, "/");
    while (padded.length % 4) {
      padded += "=";
    }
    var raw = atob(padded);
    var bytes = new Uint8Array(raw.length);
    for (var i = 0; i < raw.length; i++) {
      bytes[i] = raw.charCodeAt(i);
    }
    return bytes.buffer;
  }

  function toText(buffer) {
    var bytes = new Uint8Array(buffer);
    var raw = "";
    for (var i = 0; i < bytes.length; i++) {
      raw += String.fromCharCode(bytes[i]);
    }
    return btoa(raw).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  }

  function token(form) {
    var field = form.querySelector("input[name=__RequestVerificationToken]");
    return field ? field.value : "";
  }

  // Posts to a handler of this page with the form's antiforgery token and returns the parsed JSON, or throws.
  function options(handler, form, fields) {
    return fetch(location.pathname + "?handler=" + handler, {
      method: "POST",
      credentials: "same-origin",
      headers: { "RequestVerificationToken": token(form), "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams(fields)
    }).then(function (response) {
      return response.json().then(function (body) {
        if (!response.ok) {
          throw new Error(body && body.error ? body.error : "error");
        }
        return body;
      });
    });
  }

  function descriptors(list) {
    return (list || []).map(function (item) {
      var copy = { type: item.type, id: toBytes(item.id) };
      if (item.transports) {
        copy.transports = item.transports;
      }
      return copy;
    });
  }

  function set(form, name, value) {
    form.elements[name].value = value;
  }

  function failed(error) {
    var code = error && error.message ? error.message : "error";
    location.assign(location.pathname + "?result=" + (/^[a-z_]+$/.test(code) && code !== "error" ? encodeURIComponent(code) : "passkey_failed"));
  }

  document.querySelectorAll("form[data-passkey-enrol]").forEach(function (form) {
    form.addEventListener("submit", function (event) {
      if (form.dataset.ready === "1") {
        return;
      }
      event.preventDefault();
      say("Waiting for your device...");
      options("PasskeyEnrolOptions", form, {}).then(function (created) {
        created.challenge = toBytes(created.challenge);
        created.user.id = toBytes(created.user.id);
        created.excludeCredentials = descriptors(created.excludeCredentials);
        return navigator.credentials.create({ publicKey: created });
      }).then(function (credential) {
        set(form, "id", credential.id);
        set(form, "rawId", toText(credential.rawId));
        set(form, "attestationObject", toText(credential.response.attestationObject));
        set(form, "clientDataJson", toText(credential.response.clientDataJSON));
        set(form, "transports", credential.response.getTransports ? credential.response.getTransports().join(",") : "");
        form.dataset.ready = "1";
        form.submit();
      }).catch(failed);
    });
  });

  document.querySelectorAll("form[data-passkey-unlink]").forEach(function (form) {
    form.addEventListener("submit", function (event) {
      if (form.dataset.ready === "1") {
        return;
      }
      event.preventDefault();
      say("Waiting for your device...");
      options("PasskeyChallenge", form, { identityId: form.elements.identityId.value }).then(function (requested) {
        requested.challenge = toBytes(requested.challenge);
        requested.allowCredentials = descriptors(requested.allowCredentials);
        return navigator.credentials.get({ publicKey: requested });
      }).then(function (credential) {
        set(form, "id", credential.id);
        set(form, "rawId", toText(credential.rawId));
        set(form, "authenticatorData", toText(credential.response.authenticatorData));
        set(form, "clientDataJson", toText(credential.response.clientDataJSON));
        set(form, "signature", toText(credential.response.signature));
        set(form, "userHandle", credential.response.userHandle ? toText(credential.response.userHandle) : "");
        form.dataset.ready = "1";
        form.submit();
      }).catch(failed);
    });
  });
})();
