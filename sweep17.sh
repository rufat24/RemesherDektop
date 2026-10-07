U="/c/Program Files/Unity/Hub/Editor/6000.0.58f2/Editor/Unity.exe"
run(){ tag=$1; asset=$2; shift 2; "$U" -batchmode -nographics -projectPath Headless -executeMethod NativeRemesher.Benchmark.EditorParity.Run -asset $asset -extractor IntegerGrid -targets 4600,5000,5400 -tag _$tag "$@" -quit -logFile sw_$tag.log; echo "$tag $(grep -a -m1 "MEAN" sw_$tag.log) | $(grep -a 'holes:' sw_$tag.log | head -1 | cut -c1-160) | $(grep -a 'pairing:' sw_$tag.log | head -1 | cut -c1-160) $(grep -a 'error CS\|threw' sw_$tag.log | head -2)"; }
run hb_head head
run hc_head head -curves
run hb_body body
run hc_body body -curves
echo SWEEP17_DONE
