U="/c/Program Files/Unity/Hub/Editor/6000.0.58f2/Editor/Unity.exe"
run(){ tag=$1; asset=$2; shift 2; "$U" -batchmode -nographics -projectPath Headless -executeMethod NativeRemesher.Benchmark.EditorParity.Run -asset $asset -extractor IntegerGrid -tag _$tag "$@" -quit -logFile sw_$tag.log; echo "== $tag"; grep -a "^  $asset,IntegerGrid" sw_$tag.log | sort -u; grep -a "pairing2:" sw_$tag.log | sort -u | cut -c1-120; grep -a -o "capsNM=[0-9]*" sw_$tag.log | sort | uniq -c; grep -a "MEAN" sw_$tag.log | head -1; grep -a 'error CS\|threw' sw_$tag.log | head -2; }
run nm_head head -targets 4600,5000,5400
run nmhi_head head -targets 8000,11000
run nm_body body -targets 4600,5000,5400
echo SWEEP20_DONE
