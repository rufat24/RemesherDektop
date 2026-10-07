U="/c/Program Files/Unity/Hub/Editor/6000.0.58f2/Editor/Unity.exe"
run(){ tag=$1; asset=$2; shift 2; "$U" -batchmode -nographics -projectPath Headless -executeMethod NativeRemesher.Benchmark.EditorParity.Run -asset $asset -extractor IntegerGrid -targets 4600,5000,5400 -tag _$tag "$@" -quit -logFile sw_$tag.log; echo "$tag $(grep -a -m1 "MEAN" sw_$tag.log) $(grep -a 'error CS' sw_$tag.log | head -2)"; }
run base14_body body
run cvd_body body -curves
run base14_head head
run cvd_head head -curves
echo SWEEP14_DONE
