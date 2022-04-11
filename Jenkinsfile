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
                sh 'dotnet publish -c Altcoins-Release ./BTCPayServer/BTCPayServer.csproj --output ./public'
                stash includes: '**/public/', name: 'app'
            }
        }

        stage("deploy") {
            when { branch 'master' }
            agent { label 'production' }
            steps {
                unstash 'app'
                sh 'cp -R public/* /var/blockchains/btcpayserver'
            }
        }
    }
}
