//! TBS1 sealed shape objects (`shape-reads.md` section 12).
//!
//! [`decrypt`] authenticates the whole object before it returns any
//! plaintext, so a rejected object contributes nothing to the caller.

use std::ops::Range;

use ring::{aead, hkdf};

use super::{Error, Result};

const MAGIC: &[u8; 4] = b"TBS1";
const EPOCH: Range<usize> = 4..12;
const KEY_ID: Range<usize> = 12..38;
const SALT: Range<usize> = 38..70;
const HEADER_LEN: usize = 70;
const LENGTH_WORD_LEN: usize = 4;
const TAG_LEN: usize = 16;
const FRAME_LEN: usize = 1 << 20;
const MAX_PLAINTEXT_LEN: usize = 8 << 20;
const LAST_FRAME: u32 = 1 << 31;

/// Decrypts one TBS1 object for `handle` at `request_offset`.
///
/// `epoch_key` is the `K_e` released for `epoch` and `key_id`. Every check
/// and tag runs before this returns, so the caller applies nothing from a
/// rejected object.
pub(crate) fn decrypt(
    object: &[u8],
    handle: &str,
    request_offset: &str,
    epoch: u64,
    key_id: &str,
    epoch_key: &[u8; 32],
) -> Result<Vec<u8>> {
    let header = object
        .get(..HEADER_LEN)
        .ok_or_else(|| reject("short header"))?;
    if &header[..4] != MAGIC {
        return Err(reject("bad magic"));
    }
    if header[EPOCH] != epoch.to_be_bytes() {
        return Err(reject("epoch mismatch"));
    }
    if header[KEY_ID] != *key_id.as_bytes() {
        return Err(reject("key id mismatch"));
    }

    let object_key = zeroize::Zeroizing::new(object_key(
        epoch_key,
        &header[SALT],
        &binding(handle, request_offset),
    )?);
    let key = aead::LessSafeKey::new(
        aead::UnboundKey::new(&aead::AES_256_GCM, &object_key[..]).map_err(|_| reject("key"))?,
    );

    let mut plaintext = Vec::new();
    let mut pos = HEADER_LEN;
    let mut index = 0u64;
    loop {
        let word_bytes: [u8; LENGTH_WORD_LEN] = object
            .get(pos..pos + LENGTH_WORD_LEN)
            .and_then(|bytes| bytes.try_into().ok())
            .ok_or_else(|| reject("truncated frame"))?;
        let word = u32::from_be_bytes(word_bytes);
        let last = word & LAST_FRAME != 0;
        let len = (word & !LAST_FRAME) as usize;
        if len > FRAME_LEN {
            return Err(reject("oversized frame"));
        }
        if last && len == FRAME_LEN {
            return Err(reject("full frame marked last"));
        }
        if !last && len != FRAME_LEN {
            return Err(reject("short frame not last"));
        }
        if plaintext.len() + len > MAX_PLAINTEXT_LEN {
            return Err(reject("plaintext too large"));
        }

        let body_start = pos + LENGTH_WORD_LEN;
        let body_end = body_start + len + TAG_LEN;
        let body = object
            .get(body_start..body_end)
            .ok_or_else(|| reject("truncated frame"))?;
        let mut in_out = body.to_vec();
        let aad = frame_aad(header, index, word);
        let opened = key
            .open_in_place(
                aead::Nonce::assume_unique_for_key(frame_nonce(index)),
                aead::Aad::from(aad.as_slice()),
                &mut in_out,
            )
            .map_err(|_| reject("authentication failed"))?;
        plaintext.extend_from_slice(opened);

        pos = body_end;
        index += 1;
        if last {
            break;
        }
    }
    if pos != object.len() {
        return Err(reject("trailing bytes"));
    }
    Ok(plaintext)
}

/// `handle`, a NUL, then the canonical request offset.
fn binding(handle: &str, request_offset: &str) -> Vec<u8> {
    let mut binding = handle.as_bytes().to_vec();
    binding.push(0);
    binding.extend_from_slice(request_offset.as_bytes());
    binding
}

fn object_key(epoch_key: &[u8; 32], salt: &[u8], binding: &[u8]) -> Result<[u8; 32]> {
    let mut key = [0u8; 32];
    hkdf::Salt::new(hkdf::HKDF_SHA256, salt)
        .extract(epoch_key)
        .expand(&[&b"TBS1 object\0"[..], binding], hkdf::HKDF_SHA256)
        .and_then(|okm| okm.fill(&mut key))
        .map_err(|_| reject("hkdf"))?;
    Ok(key)
}

fn frame_nonce(index: u64) -> [u8; 12] {
    let mut nonce = [0u8; 12];
    nonce[4..].copy_from_slice(&index.to_be_bytes());
    nonce
}

fn frame_aad(header: &[u8], index: u64, word: u32) -> Vec<u8> {
    let mut aad = header.to_vec();
    aad.extend_from_slice(&index.to_be_bytes());
    aad.extend_from_slice(&word.to_be_bytes());
    aad
}

fn reject(reason: &str) -> Error {
    Error::Decode(format!("TBS1 object rejected: {reason}"))
}

#[cfg(test)]
mod tests {
    use ring::digest;
    use serde_json::Value;

    use super::*;

    const VECTORS: &str = include_str!("../../tests/fixtures/shape-vectors.json");

    /// Expected rejection reason per `tbs1_rejection_vectors` id.
    const REJECTIONS: &[(&str, &str)] = &[
        ("bad-magic", "bad magic"),
        ("tampered-epoch", "epoch mismatch"),
        ("tampered-key-id", "key id mismatch"),
        ("tampered-salt", "authentication failed"),
        ("tampered-ciphertext", "authentication failed"),
        ("tampered-tag", "authentication failed"),
        ("truncated-header", "short header"),
        ("truncated-tag", "truncated frame"),
        ("missing-last-flag", "short frame not last"),
        ("trailing-bytes", "trailing bytes"),
        ("wrong-handle", "authentication failed"),
        ("wrong-offset", "authentication failed"),
        ("wrong-key", "authentication failed"),
        ("wrong-expected-epoch", "epoch mismatch"),
        ("oversized-frame", "oversized frame"),
    ];

    fn vectors() -> Value {
        serde_json::from_str(VECTORS).expect("fixture parses")
    }

    fn unhex(s: &str) -> Vec<u8> {
        (0..s.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&s[i..i + 2], 16).expect("hex"))
            .collect()
    }

    fn key32(s: &str) -> [u8; 32] {
        unhex(s).try_into().expect("32-byte key")
    }

    fn hex(bytes: &[u8]) -> String {
        bytes.iter().map(|b| format!("{b:02x}")).collect()
    }

    fn sha256_hex(bytes: &[u8]) -> String {
        hex(digest::digest(&digest::SHA256, bytes).as_ref())
    }

    fn str_field<'a>(v: &'a Value, name: &str) -> &'a str {
        v[name].as_str().expect(name)
    }

    fn epoch_of(v: &Value) -> u64 {
        str_field(v, "epoch").parse().expect("epoch")
    }

    /// `K_e = HKDF(K_d, zero salt, "TBS1 epoch" ‖ 0 ‖ handle ‖ 0 ‖ decimal(E))`.
    fn epoch_key_for(project_key: &[u8; 32], handle: &str, epoch: u64) -> [u8; 32] {
        let mut info = b"TBS1 epoch\0".to_vec();
        info.extend_from_slice(handle.as_bytes());
        info.push(0);
        info.extend_from_slice(epoch.to_string().as_bytes());
        let mut out = [0u8; 32];
        hkdf::Salt::new(hkdf::HKDF_SHA256, &[0u8; 32])
            .extract(project_key)
            .expand(&[info.as_slice()], hkdf::HKDF_SHA256)
            .and_then(|okm| okm.fill(&mut out))
            .expect("hkdf");
        out
    }

    fn header(epoch: u64, key_id: &str, salt: &[u8]) -> Vec<u8> {
        let mut header = MAGIC.to_vec();
        header.extend_from_slice(&epoch.to_be_bytes());
        header.extend_from_slice(key_id.as_bytes());
        header.extend_from_slice(salt);
        header
    }

    /// Test-only sealing of one frame: length word ‖ ciphertext ‖ tag.
    fn seal_frame(
        object_key: &[u8; 32],
        header: &[u8],
        index: u64,
        word: u32,
        plaintext: &[u8],
    ) -> Vec<u8> {
        let key = aead::LessSafeKey::new(
            aead::UnboundKey::new(&aead::AES_256_GCM, object_key).expect("key"),
        );
        let mut in_out = plaintext.to_vec();
        key.seal_in_place_append_tag(
            aead::Nonce::assume_unique_for_key(frame_nonce(index)),
            aead::Aad::from(frame_aad(header, index, word).as_slice()),
            &mut in_out,
        )
        .expect("seal");
        let mut frame = word.to_be_bytes().to_vec();
        frame.extend_from_slice(&in_out);
        frame
    }

    /// Splits plaintext into frames, adding the empty last frame when the
    /// length is a multiple of 1 MiB (including zero).
    fn sealed_frames(object_key: &[u8; 32], header: &[u8], plaintext: &[u8]) -> Vec<Vec<u8>> {
        let mut chunks: Vec<&[u8]> = plaintext.chunks(FRAME_LEN).collect();
        if plaintext.len().is_multiple_of(FRAME_LEN) {
            chunks.push(&[]);
        }
        let last = chunks.len() - 1;
        chunks
            .iter()
            .enumerate()
            .map(|(i, chunk)| {
                let mut word = chunk.len() as u32;
                if i == last {
                    word |= LAST_FRAME;
                }
                seal_frame(object_key, header, i as u64, word, chunk)
            })
            .collect()
    }

    fn join(header: &[u8], frames: &[Vec<u8>]) -> Vec<u8> {
        let mut object = header.to_vec();
        for frame in frames {
            object.extend_from_slice(frame);
        }
        object
    }

    /// Walks well-formed frames: (word, ciphertext, tag).
    fn frames(object: &[u8]) -> Vec<(u32, &[u8], &[u8])> {
        let mut out = Vec::new();
        let mut pos = HEADER_LEN;
        loop {
            let word = u32::from_be_bytes(object[pos..pos + 4].try_into().unwrap());
            let n = (word & !LAST_FRAME) as usize;
            let body = pos + 4;
            out.push((
                word,
                &object[body..body + n],
                &object[body + n..body + n + TAG_LEN],
            ));
            pos = body + n + TAG_LEN;
            if word & LAST_FRAME != 0 {
                return out;
            }
        }
    }

    fn assert_frames(expected: &Value, object: &[u8]) {
        let expected = expected.as_array().expect("frames");
        let actual = frames(object);
        assert_eq!(expected.len(), actual.len(), "frame count");
        for (i, (f, (word, ct, tag))) in expected.iter().zip(actual).enumerate() {
            let i = i as u64;
            assert_eq!(str_field(f, "index"), i.to_string());
            assert_eq!(str_field(f, "length_word_hex"), hex(&word.to_be_bytes()));
            assert_eq!(str_field(f, "nonce_hex"), hex(&frame_nonce(i)));
            assert_eq!(
                str_field(f, "aad_hex"),
                hex(&frame_aad(&object[..HEADER_LEN], i, word))
            );
            assert_eq!(str_field(f, "ciphertext_sha256"), sha256_hex(ct));
            assert_eq!(str_field(f, "tag_hex"), hex(tag));
        }
    }

    #[test]
    fn positive_vectors_open_with_every_intermediate() {
        let v = vectors();
        let vectors = v["tbs1_vectors"].as_array().expect("tbs1_vectors");
        assert!(!vectors.is_empty());
        for t in vectors {
            let id = str_field(t, "id");
            let (handle, offset) = (str_field(t, "handle"), str_field(t, "request_offset"));
            let epoch = epoch_of(t);
            let epoch_key = key32(str_field(t, "epoch_key_hex"));
            let object = unhex(str_field(t, "tbs1_hex"));

            assert_eq!(
                epoch_key_for(&key32(str_field(t, "project_data_key_hex")), handle, epoch),
                epoch_key,
                "{id}: K_e"
            );
            assert_eq!(
                hex(&binding(handle, offset)),
                str_field(t, "binding_hex"),
                "{id}"
            );
            assert_eq!(
                hex(&object[..HEADER_LEN]),
                str_field(t, "header_hex"),
                "{id}"
            );
            let key = object_key(&epoch_key, &object[SALT], &binding(handle, offset)).unwrap();
            assert_eq!(hex(&key), str_field(t, "object_key_hex"), "{id}: K_o");
            assert_eq!(
                object.len().to_string(),
                t["ciphertext_size"].to_string(),
                "{id}"
            );
            assert_eq!(
                sha256_hex(&object),
                str_field(t, "ciphertext_sha256"),
                "{id}"
            );
            assert_frames(&t["frames"], &object);

            let plaintext = decrypt(
                &object,
                handle,
                offset,
                epoch,
                str_field(t, "key_id"),
                &epoch_key,
            )
            .unwrap_or_else(|e| panic!("{id}: {e}"));
            assert_eq!(plaintext, str_field(t, "plaintext_utf8").as_bytes(), "{id}");
        }
    }

    /// The rejection reason, with the `TBS1 object rejected: ` prefix removed.
    fn rejected(result: Result<Vec<u8>>) -> String {
        match result {
            Err(Error::Decode(msg)) => msg
                .strip_prefix("TBS1 object rejected: ")
                .expect("TBS1 rejection prefix")
                .to_string(),
            other => panic!("expected a TBS1 rejection, got {other:?}"),
        }
    }

    #[test]
    fn rejection_vectors_reject_with_the_expected_reason() {
        let v = vectors();
        let rejections = v["tbs1_rejection_vectors"].as_array().expect("rejections");
        assert_eq!(
            REJECTIONS.len(),
            rejections.len(),
            "every vector has an expected reason"
        );
        for r in rejections {
            let id = str_field(r, "id");
            let (_, reason) = REJECTIONS
                .iter()
                .find(|(name, _)| *name == id)
                .unwrap_or_else(|| panic!("{id}: no expected reason"));
            let result = decrypt(
                &unhex(str_field(r, "tbs1_hex")),
                str_field(r, "handle"),
                str_field(r, "request_offset"),
                epoch_of(r),
                str_field(r, "key_id"),
                &key32(str_field(r, "epoch_key_hex")),
            );
            assert_eq!(rejected(result), *reason, "{id}");
        }
    }

    #[test]
    fn generated_objects_match_vectors_and_reject_mutations() {
        let v = vectors();
        let generated = v["tbs1_generated_vectors"].as_array().expect("generated");
        assert!(!generated.is_empty());
        for g in generated {
            let id = str_field(g, "id");
            let (handle, offset) = (str_field(g, "handle"), str_field(g, "request_offset"));
            let (epoch, key_id) = (epoch_of(g), str_field(g, "key_id"));
            let epoch_key = key32(str_field(g, "epoch_key_hex"));
            let salt = unhex(str_field(g, "object_salt_hex"));
            let header = header(epoch, key_id, &salt);

            assert_eq!(
                epoch_key_for(&key32(str_field(g, "project_data_key_hex")), handle, epoch),
                epoch_key,
                "{id}: K_e"
            );
            assert_eq!(hex(&header), str_field(g, "header_hex"), "{id}");
            let key = object_key(&epoch_key, &salt, &binding(handle, offset)).unwrap();
            assert_eq!(hex(&key), str_field(g, "object_key_hex"), "{id}: K_o");

            let recipe = &g["plaintext_recipe"];
            let byte = unhex(str_field(recipe, "repeat_byte_hex"))[0];
            let plaintext = vec![byte; recipe["length"].as_u64().unwrap() as usize];
            let frames = sealed_frames(&key, &header, &plaintext);
            let object = join(&header, &frames);
            assert_eq!(
                object.len().to_string(),
                g["ciphertext_size"].to_string(),
                "{id}"
            );
            assert_eq!(
                sha256_hex(&object),
                str_field(g, "ciphertext_sha256"),
                "{id}"
            );
            assert_frames(&g["frames"], &object);

            let opened = decrypt(&object, handle, offset, epoch, key_id, &epoch_key)
                .unwrap_or_else(|e| panic!("{id}: {e}"));
            assert_eq!(opened, plaintext, "{id}");

            // Frame 0 authenticates first; a bad frame 1 tag must still reject the whole object.
            assert_eq!(frames.len(), 2, "{id}: needs a second frame");
            let mut tampered = frames.clone();
            let last = tampered[1].len() - 1;
            tampered[1][last] ^= 1;
            let result = decrypt(
                &join(&header, &tampered),
                handle,
                offset,
                epoch,
                key_id,
                &epoch_key,
            );
            assert_eq!(
                rejected(result),
                "authentication failed",
                "{id}: frame 1 tag"
            );

            for m in g["mutations"].as_array().expect("mutations") {
                let index = |name: &str| m[name].as_u64().unwrap() as usize;
                assert_eq!(
                    str_field(m, "expected_result"),
                    "reject_without_applying",
                    "{id}"
                );
                let (mutated, reason) = match str_field(m, "operation") {
                    "swap_frames" => {
                        let indices = m["indices"].as_array().expect("indices");
                        let (a, b) = (indices[0].as_u64().unwrap(), indices[1].as_u64().unwrap());
                        let mut frames = frames.clone();
                        frames.swap(a as usize, b as usize);
                        (join(&header, &frames), "authentication failed")
                    }
                    "drop_frame" => {
                        let mut frames = frames.clone();
                        frames.remove(index("index"));
                        (join(&header, &frames), "truncated frame")
                    }
                    "set_last_flag_recompute_tag_and_drop_following_frames" => {
                        let i = index("index");
                        let word = u32::from_be_bytes(frames[i][..4].try_into().unwrap());
                        let len = (word & !LAST_FRAME) as usize;
                        let start = i * FRAME_LEN;
                        let first = seal_frame(
                            &key,
                            &header,
                            i as u64,
                            word | LAST_FRAME,
                            &plaintext[start..start + len],
                        );
                        let mut frames = frames.clone();
                        frames[i] = first;
                        frames.truncate(i + 1);
                        (join(&header, &frames), "full frame marked last")
                    }
                    other => panic!("{id}: unknown mutation {other}"),
                };
                let result = decrypt(&mutated, handle, offset, epoch, key_id, &epoch_key);
                assert_eq!(rejected(result), reason, "{id}/{}", str_field(m, "id"));
            }
        }
    }

    #[test]
    fn plaintext_cap_is_eight_mib_exactly() {
        let v = vectors();
        let g = &v["tbs1_generated_vectors"][0];
        let (handle, offset) = (str_field(g, "handle"), str_field(g, "request_offset"));
        let (epoch, key_id) = (epoch_of(g), str_field(g, "key_id"));
        let epoch_key = key32(str_field(g, "epoch_key_hex"));
        let salt = unhex(str_field(g, "object_salt_hex"));
        let header = header(epoch, key_id, &salt);
        let key = object_key(&epoch_key, &salt, &binding(handle, offset)).unwrap();

        let at_cap = vec![0x61u8; MAX_PLAINTEXT_LEN];
        let object = join(&header, &sealed_frames(&key, &header, &at_cap));
        assert_eq!(frames(&object).len(), 9);
        let opened = decrypt(&object, handle, offset, epoch, key_id, &epoch_key).unwrap();
        assert_eq!(opened.len(), MAX_PLAINTEXT_LEN);

        let over_cap = vec![0x61u8; MAX_PLAINTEXT_LEN + 1];
        let object = join(&header, &sealed_frames(&key, &header, &over_cap));
        assert!(decrypt(&object, handle, offset, epoch, key_id, &epoch_key).is_err());
    }
}
