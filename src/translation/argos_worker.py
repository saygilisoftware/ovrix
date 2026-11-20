"""Runs CTranslate2 against one verified Argos archive without network access."""
import hashlib
import json
from pathlib import Path
import shutil
import sys
import zipfile

import ctranslate2
import sentencepiece

def run():
    source = Path(sys.argv[1])
    with source.open("rb") as archive:
        digest = hashlib.sha256()
        for chunk in iter(lambda: archive.read(1024 * 1024), b""):
            digest.update(chunk)
        if digest.hexdigest().lower() != sys.argv[2].lower():
            raise ValueError("Model SHA-256 mismatch")
        archive.seek(0)
        with zipfile.ZipFile(archive) as model:
            root = Path(sys.argv[5])
            paths = {}
            accepted = {"metadata.json", "sentencepiece.model", "model/model.bin",
                        "model/shared_vocabulary.txt", "model/config.json"}
            for entry in model.infolist():
                relative = entry.filename.partition("/")[2]
                if relative not in accepted:
                    continue
                if relative in paths or entry.file_size > 1_000_000_000:
                    raise ValueError("Invalid model file")
                target = root / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                with model.open(entry) as content, target.open("xb") as output:
                    shutil.copyfileobj(content, output)
                paths[relative] = target
            metadata = json.loads(paths["metadata.json"].read_text(encoding="utf-8"))
            if not (metadata["from_code"] == sys.argv[3] and metadata["to_code"] == sys.argv[4]):
                raise ValueError("Model language pair mismatch")
            tokenizer = sentencepiece.SentencePieceProcessor(model_file=str(paths["sentencepiece.model"]))
            translator = ctranslate2.Translator(str(root / "model"), device="cpu", intra_threads=2)
            print(json.dumps({"ready": True}), flush=True)
            for line in sys.stdin:
                request = json.loads(line)
                tokens = [tokenizer.encode(text, out_type=str) for text in request["lines"]]
                result = translator.translate_batch(tokens, beam_size=4, max_decoding_length=384)
                print(json.dumps({"text": "\n".join(tokenizer.decode(row.hypotheses[0]) for row in result)}),
                      flush=True)

if __name__ == "__main__":
    try:
        run()
    except Exception as error:
        print(json.dumps({"error": str(error)}), flush=True)
        sys.exit(1)
