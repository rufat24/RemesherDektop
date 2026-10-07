U="/c/Program Files/Unity/Hub/Editor/6000.0.58f2/Editor/Unity.exe"
run(){ tag=$1; asset=$2; shift 2; "$U" -batchmode -nographics -projectPath Headless -executeMethod NativeRemesher.Benchmark.EditorParity.Run -asset $asset -extractor IntegerGrid -targets 4600,5000,5400 -tag _$tag "$@" -quit -logFile sw_$tag.log; echo "$tag $(grep -a -m1 "MEAN" sw_$tag.log) | $(grep -a 'curve network' sw_$tag.log | sort -u | sed 's/;.*//' | head -1) $(grep -a 'error CS' sw_$tag.log | head -2)"; }
run cvao_body body -curves -curveSeed 1.5 -curveMin 6 -curveAlignOnly
run cvao_head head -curves -curveSeed 1.5 -curveMin 6 -curveAlignOnly
run cvpc_body body -curves -curveSeed 1.5 -curveMin 6 -placeCluster -curveBand 1
run cvpc_head head -curves -curveSeed 1.5 -curveMin 6 -placeCluster -curveBand 1
echo SWEEP12_DONE
