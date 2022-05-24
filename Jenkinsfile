pipeline {
    agent none
    stages {
        stage("build") {
            agent {
                docker { image 'mcr.microsoft.com/dotnet/sdk:6.0' }
            }

            environment {
                DOTNET_CLI_HOME = "/tmp/DOTNET_CLI_HOME"
                HOME = "/tmp"
            }
            steps {
                sh 'dotnet publish -c Altcoins-Release -r linux-x64 ./BTCPayServer/BTCPayServer.csproj --output ./public'
                stash includes: '**/public/', name: 'app'
            }
        }

        stage("deploy") {
              when { expression {  return env.BRANCH_NAME ==~ /release\/.*/ } }
            agent { label 'production' }
            steps {
                unstash 'app'
                sh 'sudo cp -R public/* /var/blockchains/btcpayserver'
                sh 'sudo chown -R blockchain:blockchain /var/blockchains/btcpayserver'
            }
        }
    }
}
